# DeepSeek R5 补充测试（新 supplement）

- 产品/候选/R5 evidence 未修改；所有新文件在 `docs/epub-optimization/r5-evidence-supplement/`。
- 新隔离运行目录：`D:\Temp\lyra_r5_supplement`，数据根 `D:\Temp\lyra_r5_supplement\data`，从 R5 候选运行目录复制。
- 实际模块：`reader.js=28790E1F…F61FB`，`reader-state.js=FD9E7CAD…5E8D`，`WinUIGui.dll=202954E5930FCDC7A50BD96975B84A12CBF7AA4375AC7334832D28A45F5F9480`，`WinUIGui.exe=D0F1BB58…B335`。
- 未发布、未安装、未改用户书库。

## 0. 两处提交文字更正

1. 实际 R5 `r5-evidence/SHA256SUMS` 的 SHA-256 是
   `BBD2060FF760C9E7B78BB4AAB0151A1F54A9310F85321D3CA9DCD9F2911AFC88`。
   此前回传文字中的 `BAA…` 是笔误；原件/文件本身正确，保留原提交文字快照，仅在补充中更正。
2. 真 c12fe26 identity 的实际观察是 **PID 73288 / 03:16**；R5 主报告沿用的 `PID 57968` 是旧一次运行的值。原件保留，以 GPT 复核的 PID 73288/03:16 为准；补充未改原 identity。

## 1. A08：有效图片 + 文字锚点 + 设置 + 实际 bookmark.add，关窗重开/完全退出重启

新运行目录、新 `a08-valid.epub`（有效 PNG 466×466 的简化测试图，三章：文字 c0、纯图 c1、文字 c2），经真实 R5 宿主完整链路：

- 打开 A08（PID `73628`，`a08\01-open.png`）；TOC 进入 c1 纯图页（`a08\02-image-page.png`）。
- 真实点击“加书签”命中 `x=0.81,y=0.052`：host 原始消息
  `type=bookmark.add`, `bookId=sha256:46fbd8…7925`, `bookmark.id=bm-mvr365cm-j6zqnx`，
  `REPLY bookmark.add ok=True`；state 文件 `bookmarks=[该条]`、`metadataRevision=1`、`positionRevision` 递增。
- 设置行距 2.25：`state.savePreferences {"lineHeight":2.25}` → `ok=True`；实际 `preferences.json` 写入。
- 关闭：`CLOSING` → `host.finalized ok:true` → `CLOSE_ACK revision=3`。
- 同进程重开（`a08\05-reopen-window.png`）：位置 `spineIndex=1`（c1）、`bookmarks=1`、`lineHeight=2.25`。
- 完全退出主程序再启动（重启 PID `18204`，`a08\06-reopen-after-app-restart.png`）：仍恢复 c1 图片页/书签/设置；截图显示有效红色图片，不是破图。
- 文字锚点补充（`a08\07/08-text-anchor-*`）：同一 A08 的 c0 文字页滚到 page3、`textOffset=3072`、quote 指向 TEXT-A08-A paragraph；关窗重开仍恢复该 text offset/page。
- 组合证据 `a08\09-combined-*`/`10-combined-app-restart.png`：文字锚点 page3 + line2.25 + 实际 bookmark.add + 关窗 + 完全退出重启，重启 state 仍有 bookmark 与文字位置。
- 结论：A08 本轮补为 **PASS（有效图片/文字锚点/设置/实际 bookmark.add/重开/重启均实测）**；仍只代表这本能构造的合成书，不代表所有真实书。

## 2. A19：同书双窗口 + 不同书 pending-A18 交错

### 同书双窗口
- PID `11308`，两个 reader HWND `1644356` / `8918858` 同开 `long.epub`；`a19/a19-samebook-*`。
- 两窗各自翻页并实际 `bookmark.add`；最终 state `bookmarks=2`（pageIndex4/pageIndex2 两个不同 locator）、`metadataRevision=2`、`positionRevision=5`。
- 同时关闭两窗：一窗 `CLOSE_ACK revision=7 ok:true`，另一窗 `host.finalized ok:false revision=3` / `CLOSE_ERROR`；两个书签仍都保留在磁盘。**不把双书签写成“双窗关闭都成功”**；关闭回执差异原样保留。
- 结论：A19 双书签/位置保留为 **PARTIAL/PASS 子项**，双窗同时关闭仍有一个 ack false 待 R6 判断。

### 不同书 pending-A18 交错
- PID `22236`，A=`long.epub` HWND `13963352`，B=`late.epub` HWND `17438164`；`a19/a19-differentbook-*`。
- 完整 host 消息保留 `a19-differentbook-pending.log`：A 的 4 个 `grantPreferences`, B 的多个 grant, A save `{"fontSize":18}` ok、B save `{"fontSize":32}` ok、A save `{"lineHeight":2.2}` ok。
- 最终 `a19-prefs-final.json`：`fontSize=32,lineHeight=2.2`，`preferenceOrders={22,40}`，`revision=2`。
- 时点真实文件限制：原生 UI 的保存是异步 300ms 防抖，阶段文件只抓到 A 输入后瞬间（仍只有 A font32 的早期快照）和最终文件；没能对 after-A18/after-B32/after-A-line 三个精确时点各留一份真实文件，故不伪造。完整消息链和最终文件是真；中间文件缺失明确记录。

## 3. A23：200 含图切章 + 10 换书 + 静置

- 新 Edge 浏览器/真实 DOM 渲染 + 生成的 200 章纯图片书，`a23/results.json`；不是原生 WinUI 进程趋势。
- 200 次 chapter switches：总耗时 **2,712ms**；spine 最终199；各采样 iframe 内容节点 `2`、外层 UI 节点 `307`、blob 资源 `1`、taskSets `1`、pending `0`；JS heap `3.65MB → 24.15MB → 5.24MB → 7.14MB → 7.20MB`，静置1s `7.26MB`，无持续线性增长。
- 10 次换书：10/10 完成；最终资源 `0`、taskSets `1`；静置 heap `2.60MB`。
- 未提供的边界：这不是原生 WinUI 进程 RSS/句柄趋势；A23 仍 **PARTIAL**（DOM/URL/任务/浏览器 heap 有实测，原生进程趋势未测）。

## 4. A21 / PDF

- 新 supplement runtime PID 见各 identity；`a21/a21-withbook-*`、`a21-withbook-pdf-identity.json`。
- 有书：打开 long 成功；PDF 0.3.0 有书打开成功（窗口标题 `璃鸢镜 · PDF 阅读器`）。
- 无书：Extensions 中心 2 个“打开”按钮；第二按钮（PDF 0.3.0）无书打开成功，PID `26784`，窗口标题 PDF；`a21-nobook-pdf-identity.json`。
- 多窗：同一 long 打开 2 个 reader 窗口，两窗 `CLOSE_ACK ok:true`（PID 日志见 `a21-withbook.log`）。
- PDF 只判基础打开/关闭/无书入口，不宣称 PDF 搜索或完整多窗回归。A21 仍 **PARTIAL**。

## 5. 更新后的 A 表相关项

| # | R5 补充后 | 说明 |
|---|---|---|
| A08 | **PASS** | 有效图片、文字锚点、设置、实际 bookmark.add、关窗/完全退出重启均实测 |
| A19 | PARTIAL | 同书双书签/位置保留；双窗同时关闭一 ack false；不同书字段级最终 32/2.2；pending-A18 精确阶段文件未齐 |
| A23 | PARTIAL | 200 图切章/10 换书浏览器 DOM/URL/任务/heap 有界；原生进程 RSS 趋势未测 |
| A21 | PARTIAL | c12/旧宿主/R3 client/PDF 有书无书基础、内部多窗关闭；PDF 搜索/完整多窗未做 |
| A11 | UNTESTED | 等 GPT 的 Edge 夹具复验，不重复；本轮未改产品 |
| 其他 | 沿用 R5 原表 | 不重新贴 PASS；本补充只列新增实测 |

## 6. 证据

- `native/a08/`、`native/a19/`、`native/a21/`、`a23/`
- `tools/`：A08 fixture、A23 脚本
- 精确散列：`SHA256SUMS`；摘要：`r5-supplement-manifest.json`
- 未改 R5 产品/候选/原 r5-evidence；失败/未构造项按上文保留。

## 7. A01–A25 总表（补充后；仍以 R5 主报告细项为准）

| # | 状态 | 依据/缺口 |
|---|---|---|
| A01 | PARTIAL | R4 TOC/spine 正例；完整辅助导航/片段未新增 |
| A02 | PARTIAL | NCX/无目录正例；EPUB3 深层嵌套未新增 |
| A03 | PARTIAL | 片段/中文编码正例；跨章脚注返回未新增 |
| A04 | PARTIAL | 三入口完整去重未全跑 |
| A05 | PASS | D02 + stage 旧意图失效通过 |
| A06 | PARTIAL | 坏书恢复/共享图失败/after-DOM 回滚正例；完整重试/拖入未跑 |
| A07 | PASS | R4 真实 Range/刷新恢复正例保留 |
| A08 | PASS | 本补充有效图片+文字锚点+实际 bookmark.add+设置+关窗/重启 |
| A09 | PARTIAL | 同内容改名正例；同大小不同内容未新增 |
| A10 | PARTIAL | 长段/高亮正例；设置组合未全量 |
| A11 | UNTESTED | 等 GPT Edge 夹具；不重复 |
| A12 | PARTIAL | 20 次普通翻页正例；首末/反向未全跑 |
| A13 | PARTIAL | 单 idle 正例；刷新率矩阵未做 |
| A14 | PARTIAL | 选区不翻页；复制/右键/编辑分项未全 |
| A15 | PARTIAL | 视口正例；100/150/200% 缩放未全 |
| A16 | PARTIAL | 事件/sandbox 正例；嵌套 frame/冲突 ID 未全 |
| A17 | PASS | D01/D02/D04/D05/D07 全绿；真实书搜索完成 |
| A18 | PARTIAL | 去重/上限/拒读恢复/C03；双窗书签重启未全 |
| A19 | PARTIAL | 同书双书签2；双窗同时关闭一 ack false；不同书最终32/2.2；pending 阶段文件未齐 |
| A20 | PARTIAL | S03 写失败/损坏隔离/失败快照；完整只读矩阵未全 |
| A21 | PARTIAL | c12/旧宿主/R3 client/PDF 有书无书基础、多窗关闭；PDF 搜索/完整多窗未做 |
| A22 | PASS | 373/255 首中末/字号/书签/刷新；搜索 4.03s/2.68s 完成 |
| A23 | PARTIAL | 200 图切章/10 换书浏览器 DOM/URL/任务/heap 有界；原生进程趋势未测 |
| A24 | PARTIAL | D03/pageInput/early close/host strict；搜索/动画全时序未全 |
| A25 | PASS | 干净 0.9 ZIP 解包旧宿主入口/包 smoke |