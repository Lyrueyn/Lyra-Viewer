# R3 原生补充证据（冻结后，不改候选代码）

冻结基线：`docs/epub-optimization/r3-evidence/freeze-ba6f763-20261027-161555/`  
本目录所有文件均为新增；冻结目录、`ba6f763` 源码、0.7.0 包、四个宿主源文件、两份宿主 DLL、冻结时 r3-evidence 均未改写。

运行身份（主）：
- exe SHA-256：`D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
- DLL SHA-256：`8067990EBBAF126239B1F3B8CC936D36CB3B1F47DC317D541FDF4CE9C4A9905E`
- 扩展版本：0.7.0；index.html SHA-256：`F6B244C642B1842652B4ED733116A26A3E92B1B0271BF7C1B444A1C8AF3B411D`
- 运行目录：`D:\Temp\lyra_epub_r3_candidate`
- 数据根：`D:\Temp\lyra_epub_r3_candidate\data\reader\epub`
- 真实候选进程 PID：29256（双窗/偏好）、39124（图片重开/全退出重开）、56472（位置延迟注入及关闭专项）；旧宿主进程 PID：63608
- `late.epub` 路径：`docs/epub-optimization/gpt-review-r2/image-reopen-evidence/late.epub`

---

## 1. 两个真实窗口：偏好顺序（有阻断）

操作：
1. A 打开 `long.epub`，在设置面板把字号改 18。
2. B 打开 `late.epub`，把字号改 32。
3. A 再改行距 2.2。
4. 等待 3 秒后读取真实 `data/reader/epub/preferences.json`。

结果：**FAIL（确认 GPT 审计 ①）**
- A 提交原始请求：
  - `preferences:{"fontSize":18}`
  - `fieldVersions:{"fontSize":639287149896045300}`
  - 宿主回复 `PREFERENCE_STALE`。
- B 提交 `fontSize:32` 同样 `PREFERENCE_STALE`。
- A 行距 2.2 提交为 `{"fontSize":18,"lineHeight":2.2}`，两个字段版本都被写成同一个 JS 舍入值 `639287149896045300`，宿主拒绝。
- 当时 `preferences.json` **不存在**；这三个操作都没有被保存。
- 原始日志：`native-pref-two-window-raw.log`；A/B 设置截图：`a-font18.png`、`b-font32.png`、`a-line22.png`。
- 这是真实 JS Number 往返后的 19 位 long ticks 精度缺陷，不是手填 fieldVersions。

补充观察：后续某次关闭时，一个宿主授予版本恰好与 JS 舍入值相等，`fontSize:32` 被接受并写入 `preferences-after-two-window-and-pending-close.json`；但 `lineHeight:2.2` 仍缺失，说明这是**时灵时不灵的精度缺陷**，不是稳定通过。该文件仍是 `fontSize=32`、无 `lineHeight`。

## 2. 两个真实窗口：迟到位置（PASS，注入渲染进程）

为制造“真实的旧位置发送晚于新窗口位置”，在**渲染进程级**注入延迟，不改代码：

- A 窗口 WebView2 renderer PID：`69824`
- B 窗口 WebView2 renderer PID：`59860`
- 注入点：A 完成一次翻页后，用 `NtSuspendProcess(69824)` 暂停 A 的 WebView2 renderer；B 继续操作并保存；`NtResumeProcess(69824)` 后 A 的 savePosition 才发出。
- 这不是手填消息；完整原始请求见 `position-renderer-injection-raw.log`。

结果：**PASS**
- 初始磁盘 page=2。
- A 翻页到 page3；A renderer 暂停。
- B 翻 4 页保存到 page7；`state.savePosition ok=True`。
- resume A 后，A 的迟到位置请求：`REPLY state.savePosition ok=False err=STALE_INTERACTION`。
- A 关闭时再次 flush 位置：再次 `STALE_INTERACTION`；`CLOSE_ACK`。
- 最终磁盘仍 page=7、rev=56；重开长书为 page7，截图 `position-late-reopen-B-page.png`。
- 证据：`position-renderer-injection-raw.log`、`position-late-window-raw.log`、`position-late-reopen-B-page.png`。

边界：这里注入的是 Windows 渲染进程暂停/恢复，不是改 C# 或 JS；暂停位置和 PID 已记录。A 的迟到请求由真实 reader-state 发出，宿主拒绝。

## 3. 纯图片末页保存/重开/完全重启（PASS）

`late.epub` 为 100 段文字 + 4 张 SVG 图片，末页纯图片。

- 进入末页：`spineIndex=0`、`pageIndex=26`、`locator.textOffset=null`、`locator.elementPath=[103]`、`quote=""`。
- 截图 `late-image-a-last.png` 显示两张蓝色图片（LATE IMAGE）。
- 关闭 reader：`CLOSING`/`CLOSE_ACK`；再点书库卡片重开：截图 `late-image-reopened-library.png` 仍是末页图片，页 27/27。
- 完全退出主程序并重启后，再开 `late.epub`：截图 `late-image-after-full-restart.png` 仍为末页图片；磁盘状态仍 `elementPath:[103], textOffset:null, pageIndex:26`。
- 证据：三张截图 + `native-after-restart-raw.log` + `final-runtime-raw.log` 中的 `state.load`/`book.ready`。

## 4. 关闭场景

### 4.1 搜索进行中关闭（PASS，约 96 ms）

- 打开 reader，Ctrl+F，输入 `LATE`，60ms 后通过 WindowPattern.Close。
- 从 `CLOSING` 到 `CLOSE_ACK`：约 96ms（实测停止等待 ms=96）。
- 日志 `close-during-search-raw.log` 中 `CLOSING` → `host.finalized` → `CLOSE_ACK`。
- 注：这份日志也携带同一进程早先 A 窗口 pending prefs 的 final flush（savePreferences 被 PREFERENCE_STALE 拒绝），说明旧 pending 会在关闭时尝试提交。

### 4.2 加载中关闭（FAIL，窗口悬挂）

- 在窗口创建后约 85ms、扩展尚未完成加载时关闭。
- 事实（`close-during-loading-stuck-facts.txt`）：
  - `CLOSING request=dc2068434a8a4b0ca125d1377aca07c8` at 00:28:07.289；
  - 之后扩展的 `app.ready`/`hello`/`state.load`/`book.ready` 才到达；
  - 10 秒以上没有 `host.finalized`、`CLOSE_ACK`、`CLOSE_TIMEOUT`、`CLOSE_ERROR`；
  - reader 窗口 handle 24646914 仍可见；截图 `close-during-loading-stuck-window.png`。
- 原始日志：`close-during-loading-after10s.log`（与 `close-during-loading-raw.log` 同段）。
- 结论：**加载中关闭会挂住且关闭 deadline 没有生效**；这与已加载窗口的 96ms ack 形成对照，是独立于 GPT ①–④ 的新阻断。

### 4.3 等待偏好回执时关闭（FAIL：ack 与真实保存不一致）

- 打开长书，设置行距 2.2，随即将窗口关闭（仍在 debounce/在途窗口内）。
- 关闭耗时约 84ms。
- 日志 `close-with-pending-prefs-raw.log`：
  - `CLOSING request=2b...`
  - `state.savePreferences` `{"lineHeight":2.2}` fieldVersion `639287157498458100`
  - 宿主 `REPLY ... ok=False err=PREFERENCE_STALE`
  - 扩展 `host.finalized ok:true`
  - `CLOSE_ACK revision=0`
- 实际偏好文件没有 `lineHeight`（`preferences-after-two-window-and-pending-close.json`）。
- 结论：**待偏好回执时关闭，宿主拒绝保存但扩展仍 ack ok:true**；与 GPT ②③ 独立复现一致，且真实落盘失败。

## 5. 兼容性（原生）

### 5.1 新 R3 宿主 + 旧 EPUB 0.5.26

运行目录 `D:\Temp\lyra_r3_compat_newhost`；宿主 DLL `8067990...`，`extensions/epub` = 0.5.26，PDF=0.3.0。

- 有书（长书）：`app.ready`/`hello`/`state.load`/`book.ready` 全部正常；正文可见，翻页截图 `compat-newhost-old-epub-withbook.png`、`compat-newhost-old-epub-after-turn.png`。基本交互通过。
- 无书：主界面扩展 Tab 的“打开”按钮打开空 reader；空状态提示/按钮正常，截图 `compat-newhost-nobook-0.png`。

### 5.2 新 R3 宿主 + PDF 0.3.0

- 有书：PDF 文件 `ASIO SDK 2.3.pdf`，50 页；打开、目录/缩略图、页码 1/50 正常；宿主收到 legacy `{"type":"lyra-book-ready"}`；截图 `compat-newhost-pdf030-withbook.png`。
- 无书：扩展 Tab PDF“打开”按钮打开空窗口，截图 `compat-newhost-nobook-1.png`。

### 5.3 新 EPUB 0.7.0 + 原 1.1.12 宿主

运行目录 `D:\Temp\lyra_r3_compat_oldhost`；exe SHA `CFB5FE9E...`、DLL SHA `3D330EDC...`（原 1.1.12 宿主），扩展=0.7.0，PDF=0.3.0。

- 打开长书正常；页脚可见诚实降级提示：
  `此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口...`
- 点击正文后发送 Right，页码 1/9 → 3/9，基本交互正常。
- 截图：`compat-oldhost-new-epub-open.png`、`compat-oldhost-new-epub-after-turn2.png`。
- 旧宿主没有 `LYRA_READER_DEBUG` 日志，故本项证据为截图和实际交互，不是宿主日志。

## 6. A 编号校正（接受 GPT 指正）

- A08 是“原生关闭/重开、重启恢复”，不是 idle rAF；原报告把它写成 idle 覆盖不准确。
- A19 是“多窗口顺序（偏好/位置）”，不是关闭 ack；关闭 ack 应单独归属 finalize 场景。
- A23 需要含图压力/真实图片页证据，不能拿单项资源模块测试标完整 PASS。
- 更正写入 `R3-A编号校正-补充.md`，不覆盖冻结报告。

---

## 6.1 旧兼容样本更正（GPT 2026-10-28 指正后）

上一轮 5.1/5.3 的“旧”样本身份不成立：

- `D:\Temp\lyra_r3_compat_newhost\extensions\epub\index.html` 实际只有 109 行、SHA `5A38641D...`，是模块化入口；不是任务书 0.5.26 基线。旧截图虽有 extension.json = 0.5.26，但不能作为基线证据。
- `D:\Temp\lyra_build_112b` 的实际 WebView2 行是 `1.1.12+61b53b24...`，不是原基线 b978661；旧 compat-oldhost 截图不能计入原 1.1.12 宿主。

新增 `compat-baseline-c12fe26/`，从 **git blob 原始 LF 字节**提取真基线：

- EPUB 0.5.26 commit `c12fe26275cdec67922cccb64d9b5f2b4c269cd2`
- index.html 675 行 / 30973 bytes / SHA-256 `88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A`
- 新 R3 宿主 + 真旧扩展有书/无书均通过基础兼容，PID 72996；`lyra-book-ready` 被接受；旧扩展关闭由新宿主按 1.5s `CLOSE_TIMEOUT` 结束。
- 原 1.1.12 b978661 宿主：PID 55100，DLL `57CC...F3CD`，WebView2 子进程实际 `1.1.12+b978661...`；新 0.7.0 可读、可翻页，并诚实降级为本地模式。
- 附加真旧/真旧：PID 69852，基础打开通过。

详见 `compat-baseline-c12fe26/README-真基线补测.md`、`compat-identity-CORRECTION.md`、`compat-baseline-c12fe26/results.json` 和 `native-supplement-results-v2.json`。旧材料保留，但旧 compat 的 0.5.26/1.1.12 身份不再引用。

## 7. 文件清单

关键新增文件：
- `native-pref-two-window-raw.log`、`a-font18.png`、`b-font32.png`、`a-line22.png`
- `preferences-after-two-window-and-pending-close.json`
- `position-renderer-injection-raw.log`、`position-late-reopen-B-page.png`
- `late-image-a-last.png`、`late-image-reopened-library.png`、`late-image-after-full-restart.png`
- `close-during-search-raw.log`
- `close-during-loading-stuck-facts.txt`、`close-during-loading-stuck-window.png`、`close-during-loading-after10s.log`
- `close-with-pending-prefs-raw.log`
- `compat-newhost-*`、`compat-oldhost-*`
- `native-run-identity.json`、`native-run-identity-2.json`
- `SHA256SUMS`（本目录）

边界声明：本目录是原生 Windows 候选进程 + 实际 WebView2 的证据；延迟位置项唯一注入点是 A 的 WebView2 renderer PID 69824 的暂停/恢复。没有改候选源码、没有手填 fieldVersions。
