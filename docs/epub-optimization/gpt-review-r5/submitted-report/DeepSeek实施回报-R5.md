# DeepSeek 实施回报 · R5

- 任务书：`docs/epub-optimization/GPT验收结论-R4暨DeepSeek返工任务书-R5.md`，SHA-256 `76CFE3AA0C9C7A0FCD8B7E76EBCF259D0116E0A5E2C5797D8AF293C68BAAFDAA`
- 分支 / 提交：`D:\Temp\Lyra-Extension-Epub`，`r5` / `76ced4c17efa4526fbc8c00e3fff5261959a7fe7`
- 候选包：`docs/epub-optimization/candidates-r5/lyra-ext-epub-0.9.0-candidate.zip`
  - SHA-256 `60CC97BF2721BB0239122275B307833D871548C985A56068E649F3D3A1517B80`，95,702 bytes，版本 `0.9.0`
- 宿主候选：`D:\Temp\lyra_epub_r5_candidate`
  - `WinUIGui.dll` `202954E5930FCDC7A50BD96975B84A12CBF7AA4375AC7334832D28A45F5F9480`
  - `WinUIGui.exe` `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
  - 构建目录 `D:\Temp\lyra_epub_host_r5`
- 运行模块（实际加载，不只 entry）：
  - `reader.js` `28790E1FB4CD5E04302C26217D80B293D8BDEDFC3CD4E1551C9A02C0D93F61FB`
  - `reader-state.js` `FD9E7CAD46398D393966358D0813668975D8F93BF915A573E06536C747175E8D`
  - `extension.json` `0.9.0`
- 设计：`r5-evidence/状态设计-R5.md`
- 证据：`docs/epub-optimization/r5-evidence/`；不存在发布/部署/用户安装改动。

## 1. 状态设计（已先行落盘）

- 偏好字段级状态：`applied` / `idempotent` / `superseded`；superseded 字段不阻塞其它有效字段；完成凭据与活跃授权分表，容量回收后可幂等重试。
- 位置：`positionRevision` 只随真实位置保存递增；`metadataRevision` 只随书签等元数据递增；客户端失败快照只比较 `positionRevision`；token 在操作发起时冻结，await 后不补全局 token。
- 关闭：冻结最后页码输入/动画目标，停止所有输入/定时器/旧导航；ready=false 使用最后有效阅读快照；ack 必须要求冻结目标已确认、无 pending/failed。
- 导航：`navOp` 身份贯穿 prepare/资源/DOM/layout/定位/高亮/保存；新文件/查询/关闭立即失效旧操作。
- 搜索：首词起始窗口 + 全文续接，等价非重叠整串匹配；搜索与显示共用一份保留可见性语义的正文模型。
- 资源：准备任务与已显示/回滚引用分开，释放按原 book+owner+全局存活引用；失败 prepare 不撤销仍显示的共享图。

## 2. R5-01 ~ R5-03（优先数据与关闭）

### R5-01 字段级偏好结算、幂等完成与 scope

- 宿主 `SavePreferences` 返回 `applied/idempotent/superseded`；旧 font18 + 新 line2.2 同事务：`applied:["lineHeight"]`、`superseded:["fontSize"]`，磁盘为 32/2.2，不再整笔 `PREFERENCE_STALE` 让 line 丢失。
- 完成凭据独立于活跃授权：`PreferenceCompletions` 有界缓存；4095 未完成授权 + font28 完成 → 新 line 授权回收 font28 活跃 token → 原 font28 用原 token 重试仍返回 `applied/idempotent`，不返回 `MISSING_GRANT`。
- `scope` 只支持 `global`；`book:foreign` 返回 `UNSUPPORTED_SCOPE` 且 preference 文件前后 SHA 相同。
- 证据：`r5-evidence/host-audit/r5-host-results.json` 的 `R5-PREF-FIELD-SUPERSEDED`、`R5-PREF-COMPLETION-CAPACITY`、`R5-PREF-SCOPE` 全 PASS。
- 客户端字段级清 pending：`r5-evidence/client-close/results.json` `R5-CLIENT-FIELD-SUPERSEDED` PASS，host 返回 line 应用/font 取代，客户端分别处理。
- 原生 fast A/B：PID `52412`，最终磁盘 32/2.2（`native/native-fast-A18-B32-A22-raw.log`，host 原始 save 32 与 line2.2 均 ok）；该次 A18 实际已先 flush，不是 pending-A18；pending-A18 的原生精确时序仍未稳定构造，不冒充。字段级混合由上面的宿主审计和客户端测试覆盖。

### R5-02 位置提交版本与失败快照

- 宿主 `state.load` 返回 `revision/positionRevision/metadataRevision`；`state.interact` 不再改任何 revision；`state.savePosition` 才 +positionRevision；书签只 +metadataRevision。
- 宿主 S03：`positionRevision=0`，interact 后仍 0，位置文件只读保存 `WRITE_FAILED`，重读仍 `positionRevision=0/position:null`；客户端原失败快照不再因授权 revision 被误删（宿主审计 `R5-POS-REVISION-SEPARATION` / `R5-POS-S03-FAILED-WRITE` PASS）。
- `reader.js` 冻结 tokenPromise 返回 token、sequence、operationId、basePositionRevision；`resolveFailedSnapshotOnOpen` 使用 `loaded.positionRevision`，重试沿用 snapshot sequence/operationId，不重新取全局 token。
- 普通纯图旧 ack 正例保留：R5 image/races 回归 C01–C06 / C02–C03 的 defect 均为 0（`r5-evidence/image-ack`、`races-v2`）。
- 边界：S03 的真实客户端“重开原书可见页”没有新增原生截图；本轮以宿主实际文件 + 客户端代码链证明，真实 WinUI 重开未冒充。

### R5-03 最终关闭冻结目标

- `finishAndAck` 现在先清 `pageInputTimer` 并把待输入页码结算进冻结位置，再清 save/prefs/search 定时器、turn queue、搜索、动画，并 `navGeneration++` 使旧导航失效；`ready=false` 时用 lastStablePosition 保存，不再把“没有 ready”当无待保存。
- 客户端 fake-host：
  - `R5-CLIENT-CLOSE-PAGEINPUT`：输入第 9 页后在 420ms 防抖前关闭，最终保存 `pageIndex=8`，ack true，modified false。
  - `R5-CLIENT-CLOSE-DURING-READ`：第 2 页 pending → 下一章读取挂起 → finalize；最终保存原第 2 页（spine0/page1），ack true，释放后旧 read 没有把正文推进到下一章（spine 仍0）。
- 真实 Edge D03 重跑：finalize 后保存的是原页，`modified:false`，释放后 late spine 仍0，defect false。
- 原生普通 pending 设置关闭：`native/r5-native-pending-close.log` 有 `CLOSE_ACK revision=3`，实际 `lineHeight=2.25` 写盘；`host.finalized ok:true`。
- 宿主审计：finalize 缺 session 不关、错 request 不关、精确 session+request 的 `ok:false` 关闭；早期 close timer 独立通过。
- 边界：真实 WinUI 的 pageInput 防抖关闭未单独原生复跑；以真实 Edge/fake-host 全链和宿主审计记录。

## 3. R5-04 ~ R5-08

### R5-04 查询/开书意图贯穿

- 新文件 intent 在文件字节读取前就 `navGeneration++` 并 invalidateSearch；`navigateToSpine` 的 `navStillCurrent` 在 prepare、资源 wait、setSection 前后、layout/goToPage 前、highlight 前检查 book/openIntent/query/navGen/finalizing；`paginator.setSection` 接受 `isCancelled` 回调，在资源等待后和 layout 后再次检查。
- 真实 Edge D02：改 NEWQUERY 后旧查询点击不再换正文，`spine` 保持0，defect false；真实 D01/D04/D05/D07 均无 defect。
- 模型 stage audit：`N-prepare-*`/`NP-*`、`N-resources-*`/`NP-*` 全部 PASS；资源失效后不再 layout/goToPage；旧 DOM 提交后无继续定位。
- 边界：DOM 已提交但 invalidation 后的产品回滚策略是“停止后续副作用、保持当前可用状态，不把旧导航标成功”；没有新增重建旧 DOM 的原生证据，已在设计边界记录。

### R5-05 真实重叠搜索

- `search()` 改为：首词起点在窗口内枚举，剩余整串在全文上续接匹配；跨块匹配不会丢，也不会因窗口重叠重复计数；支持空白跨块、emoji、UTF-16 偏移、1001 边界。
- 模型 `X01/X02/X03/X04/X05` 全 PASS；边界测试 `audits/boundary-results.json`：
  - 真实块界 `65844` 的 CROSSBOUNDARY 三章均找到，UI 显示“找到 3 处 · 已扫描 3/3 章”。
  - 1001 处保留 1000 + truncated。
- 真实 Edge D01：`boundary.epub` 3 章 CROSSBOUNDARY，UI 3 个按钮，defect false。
- 查询长度 >4096 明确返回 `QUERY_TOO_LONG` 并给 UI 提示；74001 字符边界不作为“块缺失”证据。

### R5-06 统一有效文本与可见性

- 清洗器只保留 `style` 中的 `display:none` 和 `visibility:hidden|visible|collapse`，使清洗前后可见语义一致；`buildTextModel` 区分 display:none（子不可恢复）与 visibility（子可 visible 覆盖）；隐藏 `<br>` 不换行；aria-hidden 视觉保留；defs/desc/title/symbol/use 跳过。
- 模型 X06：inline display、hidden br、SVG inline visibility 的 cold/model/display 一致；X07 中 visible override 被保留，use/symbol 被排除。
- 真实 Edge D04/D05/D07 defect 均 false：D04 TARGET 高亮不再包含 HIDDEN；D05 SVG hidden tspan 不再被 Range 跨入；D07 hidden 父 + visible 子可被搜索。
- 搜索结果 Range 边界修正：起点在边界取下一可见 segment、终点取当前 segment，避免把隐藏 DOM 文本包进 Range。

### R5-07 资源引用所有权

- `releaseTask` 保留所有其它存活 task 的路径，而不是只保留 `_currentTaskId`；准备失败时旧显示 owner 的共享 URL 不被撤销；成功提交后旧唯一资源照常回收。
- 模型 X08 PASS（同书下一章共享 shared.png，prepare 后段失败，旧 URL 未 revoke、`_resources` 保留）；stage `N-SHARED-FAIL` PASS；真实 Edge D06 defect false（旧正文 URL 仍 fetchOK）。
- 早 read 失败 40 次、跨书 owner2、200 次图片切章 R05 全 PASS；原生 200 含图切章 + 10 换书进程趋势未新增，A23 因此保留 PARTIAL。

### R5-08 同书书签拒读恢复

- `_localBookmarkState` 不再先拒绝旧的损坏标记，先读取当前原件；成功读取后清除旧 bookmark corrupt 标志；临时 `localStorage.getItem` 抛错后恢复，可用原健康书签并继续新增。
- 客户端 fake-host：`R5-CLIENT-BOOKMARK-RECOVERY` PASS；坏 A→健康 B→回 A、损坏未恢复、未知版本的原语义保留在本地状态代码。
- 证据：`r5-evidence/client-close/results.json`。

## 4. 真机/兼容/包

- 最终 R5 宿主 + 真 c12fe26：`compat/r5-finalhost-c12-withbook-identity.json`，PID 57968，hostDll `202954…9480`，`index.html` 675行 SHA `88FA…9721A`，legacy `lyra-book-ready`。
- R5 宿主 + R3 0.7.0 客户端：PID 见 `r5-host-r3client-identity.json`；日志 `version:1` → `LEGACY_HELLO_INCOMPATIBLE`，页面 local 提示；未错称 host 模式。
- b978661 旧宿主 + 干净 0.9.0 ZIP：`r5-oldhost-b978-r5zip090-identity.json`，PID 37304，DLL `57CC…AAF3CD`，WebView `1.1.12+b978661…`，干净模块 reader `2879…F61FB`、state `FD9E…5E8D`、index `F6B2…B411D`，local 提示，基本打开。
- R5 宿主 + PDF 0.3.0：基本打开，`compat/r5-host-pdf030-*`；不宣称 PDF 搜索/多窗全回归。
- 旧/旧不变组合引用 R3 已接受基线 `r3-evidence-supplement/compat-baseline-c12fe26/`；R5 未重跑、不扩大含义。
- A22：两本真实书 373/255 章，首中末、字号、书签、刷新重开 PASS；真实书搜索完成条件实测 **4,030ms / 2,678ms**，结果 1000/591，无 60s 断言（`realbooks/r4-real-books-results.json`）。
- A25：干净 0.9.0 ZIP 解包到旧宿主实际入口打开成功；package smoke 亦 PASS。
- A08：沿用 R4 原生 c1 纯图页/line2.2 关窗/重启恢复证据；**书签未恢复、测试 PNG 破图**，本轮未新增有效图片+书签同时恢复，仍 PARTIAL。
- A11：真正晚于 ready/恢复意图的资源/字体整链未测，UNTESTED。
- A23：只有模块/旧证据，无本轮原生 200 次含图切章/10 换书进程趋势，PARTIAL。

## 5. 测试汇总

- 宿主审计：`host-audit/r5-host-results.json` 7/7 PASS。
- 模型：`model/model-results.json` 22 项 0 FAIL、1 OBSERVATION；`reader-flow-results.json` 8/8。
- 边界：`audits/boundary-results.json` X01 PASS、X02 PASS、X03 PASS、X04 PASS、X05 PASS；X07/X10/X11 OBSERVATION。
- stage：`audits/reader-stage-results.json` 16/16 PASS。
- 真实 Edge D01/D02/D03：defect false；D04/D05/D06/D07：defect false。
- 真实 Edge 回归：image-ack 6 行 0 defect；races-v2 2 行 0 defect。
- fake-host 客户端：4/4 PASS（字段级/页码关闭/读章关闭/书签恢复）。
- 宿主实际原生：fast A/B 最终 32/2.2（raw log）；普通 pending 设置关闭 ack=true line2.25；compat/R5/PDF 身份记录。
- 没有发布、部署、安装；R4 freeze 和 R4 证据未改（运行中曾误写 R4 的 image/races/model 文件，已按 GPT 冻结副本完整恢复，SHA256SUMS 仍 `A7BC43…C883F` 且 bad=0）。

## 6. 原 A01–A25 状态（R5）

> “PASS”只对应本表范围；PARTIAL/UNTESTED 不写成产品失败，其中 A05/A17/A19/A24 的 R4 明确反例已在 R5 关闭或由新证据覆盖。

| # | 状态 | 依据/缺口 |
|---|---|---|
| A01 | PARTIAL | R4 TOC/spine 正例保留；辅助导航/片段全链未重新补 |
| A02 | PARTIAL | NCX/无目录正例；“EPUB3 嵌套 / 前置内容”未新增 |
| A03 | PARTIAL | 片段/编码正例；跨章脚注返回未新增 |
| A04 | PARTIAL | 三入口完整去重/分阶段未全跑 |
| A05 | PASS | D02 查询失效不换正文；stage prepare/resources 全部通过；慢章正例在 R4 |
| A06 | PARTIAL | 好 B/坏 A、书签恢复、共享图失败、after-DOM 回滚正例；完整重试/拖入未全跑 |
| A07 | PASS | R4 真实文字 Range/刷新恢复正例保留，R5 锚点回归未退化 |
| A08 | PARTIAL | R4 原生 c1/line2.2 恢复；书签未添加、PNG 破图，未完成完整同时恢复 |
| A09 | PARTIAL | 同内容改名共享正例；同名同大小不同内容未新增 |
| A10 | PARTIAL | 文字/长段/高亮正例；字号行距宽度侧栏组合未全量 |
| A11 | UNTESTED | 真正晚资源/字体整链未构造；R3 不替代 |
| A12 | PARTIAL | 20 次普通翻页正例；首末/反向/跨章未全跑 |
| A13 | PARTIAL | 单 idle 正例；60/144/240Hz 矩阵未做 |
| A14 | PARTIAL | 选区不翻页；复制/右键/链接/编辑输入分项未全跑 |
| A15 | PARTIAL | 视口正例；100/150/200% 缩放未全跑 |
| A16 | PARTIAL | 事件/sandbox/白名单正例；嵌套框架/冲突ID/宿主接口隔离未全跑 |
| A17 | PASS | D01/D02/D04/D05/D07 全绿；真实书搜索完成耗时记录 |
| A18 | PARTIAL | 去重/分页/上限/拒读恢复/C03 正例；原生重启同时恢复未新增 |
| A19 | PARTIAL | 普通双窗 32/2.2、字段级 mixed、宿主 S01/S05 正例；pending-A18 原生精确时序未稳构成，书签双窗未全 |
| A20 | PARTIAL | S03 写失败/损坏隔离/失败快照正例；完整只读磁盘矩阵未全 |
| A21 | PARTIAL | c12/R3 client/b978 clean0.9/PDF 基本；PDF 搜索/多窗与 c12 无书未全；旧/旧引用 R3 |
| A22 | PASS | 373/255 首中末/字号/书签/刷新；搜索完成 4.0s/2.7s，结果 1000/591 |
| A23 | PARTIAL | 模块 200 次图片切章/10 换书有界；无本轮原生 DOM/URL/进程趋势 |
| A24 | PARTIAL | D03、pageInput关闭、early close、host strict 正例；搜索/动画/原生全时序未全 |
| A25 | PASS | 干净 0.9.0 ZIP 解包、旧宿主实际入口打开、包 smoke |

## 7. 明确未覆盖/边界

1. A11 真实晚资源、A08 有效图片+书签同时恢复、A23 原生进程趋势本轮未做；不把 R4/R3 模块证据替代。
2. pending-A18 原生精确时序未稳定构造；字段级修复由宿主审计/客户端 fake-host 证明；普通原生 32/2.2 是正例。
3. R5-04 DOM 已提交后失效的产品回滚是“停止后续副作用并保持可用”，不是重新构造旧 DOM 的完整回滚动画证据。
4. 测试未证明的场景继续 PARTIAL/UNTESTED；故障注入/fake-host 明确与实际 WinUI 区分。
5. 没有修改用户书库/安装；无发布。
