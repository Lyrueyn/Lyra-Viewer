# DeepSeek 补充测试报告 · R4 冻结期（只写 supplement）

- 本轮不改 R4 候选、不改 `r4-evidence`；所有新文件只在 `docs/epub-optimization/r4-evidence-supplement/`。
- 运行隔离：`D:\Temp\lyra_epub_r4_supplement`（从 R4 候选目录复制，数据根 `D:\Temp\lyra_epub_r4_supplement\data`）。
- 扩展模块统一为 0.8.0 候选包：`reader.js = 659A466FF7693FBEDD9A0DAEDBCCFBA5BE35389E9685CCCF5178A1C9084DACE4`，`reader-state.js = A790F06F1585291094596F5E0C6075EEEEEF94E8EFF5A7347AE760EC4149FCC0`，`extension.json = 0.8.0`，候选包 SHA-256 `B128B9DFBE9D7A1085CC3505F9B3EDC645847CFA24DA33958EB26EDD685F184B`。
- 宿主模块：`WinUIGui.dll = 9EBA643FA6E3933B5A3F86AFC1BE424FFAA08A09BF11B9AC89D2C451E3A9DF95`，`WinUIGui.exe = D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`（除明确标注旧宿主 57CC… 的测试）。

## 1. 兼容身份更正（冻结原证据不再引用错误 DSC）

### 1.1 最终 9EBA 宿主 + 真 c12fe26 有书

- 运行目录：`D:\Temp\lyra_r4_supp_finalhost_c12`
- PID `57968`；`hostDll=9EBA643FA6E3933B5A3F86AFC1BE424FFAA08A09BF11B9AC89D2C451E3A9DF95`，`hostExe=D0F1…B335`。
- 扩展是真 c12fe26：`extension.json=0.5.26`，`index.html` 675 行，SHA `88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A`。
- 原始 host 日志：`[02:12:19.062] NAV …name=long.epub` → `[02:12:19.653] raw="lyra-book-ready"`；截图 `compat/finalhost-c12-withbook.png`；身份 `compat/finalhost-c12-withbook-identity.json`，日志 `compat/finalhost-c12-withbook-raw.log`。
- 更正：冻结的 `compat/r4host-c12-withbook-identity.json` 里 hostDll 是 `7C79C1996D99…AE49C47`，不是最终 `9EBA…DF95`；以本补充为准，不覆盖原文件。

### 1.2 原 b978661 旧宿主 + 干净 0.8.0 ZIP

- 把 `candidates-r4/lyra-ext-epub-0.8.0-candidate.zip` 完整解压到新隔离目录，再放到旧宿主运行目录 `D:\Temp\lyra_r4_supp_oldhost_080`。
- `hostDll=57CC576547C1FE8BC51780C722E6F39B7F00838A6CF75B19AAAC539DA2AAF3CD`，`hostExe=D0F1…B335`；WebView2 原始行 `--webview-exe-version=1.1.12+b978661b8a9ec7a9c9fee87d5d323ea1db2bc5ae`。
- 干净解包模块：
  - `extension.json = 0.8.0`
  - `reader.js = 659A466FF7693FBEDD9A0DAEDBCCFBA5BE35389E9685CCCF5178A1C9084DACE4`
  - `reader-state.js = A790F06F1585291094596F5E0C6075EEEEEF94E8EFF5A7347AE760EC4149FCC0`
  - `index.html = F6B244C642B1842652B4ED733116A26A3E92B1B0271BF7C1B444A1C8AF3B411D`
- 首次启动 PID `73980`：`compat/oldhost-b978661-r4zip080-identity.json`，截图 `compat/oldhost057-dll-r4zip-open.png`；footer 实际显示“此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口…”（local 降级）。
- 同一干净包再次启动 PID `50636`，正文点击后 `ArrowRight`：截图 `compat/oldhost057-r4zip080-page2-pid.png`，footer 为“第1/2章 第2/9页”，证明基本翻页；身份 `compat/oldhost-b978661-r4zip080-page-turn-identity.json`。
- 更正：原 `oldhost-b978661-r4ext-identity.json` 的 extensionVersion 0.7.0 是混合运行目录元数据；旧宿主 + 干净 0.8.0 包以本节为准。新宿主 + R3 0.7.0 的既有 identity 未改，仍以冻结证据为准。

## 2. 原生双窗 A18 → B32 → A22（正常顺序）

- PID `50020`；A=long.epub hwnd `10555100`，session `bd5d93b5dadf4b23972c82f28fc3afee`；B=late.epub hwnd `8327748`，session `4dec10d19dd74a1bb3a68e5d20bc1f2a`；data `D:\Temp\lyra_epub_r4_supplement\data`。
- 实际控件截图：`windows/native-A-font18.png`（字号 18）、`windows/native-B-font32.png`（字号 32）、`windows/native-A-line22.png`（行距 2.20）。
- 原始顺序（`windows/native-two-window-A18-B32-A22-raw.log`）：
  1. 02:02:51.645 A session `bd5d…` grant field fontSize，activationEpoch 3
  2. 02:02:52.232 A save `{"fontSize":18}` grant `pg-3ea4b9df…` → `ok=True`
  3. 02:02:56.534 B grant fontSize epoch 3
  4. 02:02:57.560 B save `{"fontSize":32}` → `ok=True`
  5. 02:03:01.854 A grant field lineHeight，activationEpoch 5
  6. 02:03:02.869 A save `{"lineHeight":2.2}` grant `pg-8fb64268…` → `ok=True`
- 最终磁盘：`fontSize=32`，`lineHeight=2.2`，`preferenceOrders={"fontSize":22,"lineHeight":40}`，revision 3；身份见 `windows/native-two-window-identity.json`。
- 这是正常正例：A18 已在切 B 前 flush，不能代表 pending-A18。

## 3. pending-A18 原生快速顺序（R4 剩余缺陷，不修）

- 为让 A18 未 flush，预先打开两个设置面板，再快速 A18 → B32 → A2.2。
- PID `50864`；A=long hwnd `3016756` session `f1504a053e974765846c104e02954a49`；B=late hwnd `12783856` session `e44e3c445c8c4248a6df86da136c6862`；日志 `windows/native-fast-A18-B32-A22-raw.log`，身份 `windows/native-fast-two-window-identity.json`。
- 实际发生：
  1. A fontSize grant（未保存）→ B font32 grant/save `ok=True`（02:04:07.538）
  2. A lineHeight grant（02:04:07.360）
  3. A 的最后一次 save 把旧 font18 和新 lineHeight 放在同一事务：
     `preferences={"fontSize":18,"lineHeight":2.2}`，`grants={fontSize:pg-5bd…, lineHeight:pg-f1f1…}`
  4. host 返回 `ok=False PREFERENCE_STALE …fontSize`
- 最终磁盘只有 `fontSize=32`，`lineHeight` 未写；A 的 lineHeight pending 未完成。**这不是补测伪造的失败，是实际原生复现**；对应 R5 的偏好互斥/事务拆分问题。
- 未成功制造的边界：真正“18 保存回执在途 → 输入 32 → 关闭”的 ack-in-flight 时序未在原生中稳定构造；宿主专项的 controlled 用例另记，不冒充原生。

## 4. 真实存储写失败后的关闭

- 隔离候选数据目录中把 `data/reader/epub/preferences.json` 设为只读（`attrib +R`，测试后恢复），启动 PID `40224`；打开 long，行距改 2.2，立即关窗。
- 原始日志 `native-failure/native-write-failure-close-raw.log`：
  - `state.savePreferences {"lineHeight":2.2}` → `REPLY ok=False err=WRITE_FAILED 状态写入失败：Access to the path is denied.`
  - CLOSING 后再次 save → 同样 `WRITE_FAILED`
  - `host.finalized ... "ok":false,"revision":0` → `CLOSE_ERROR ... revision=0`
- 文件 SHA 前后相同：`6024154CB66AC2D534C563B475BB743AF799711CC8AAE67BB557A8131E5D904A`；`preferences-before.json` / `preferences-after.json`；身份 `native-failure/native-write-failure-identity.json`。

## 5. A08 / A11 / A22 / A23 / A25

### A08（新 R4 文字页 + 纯图片页 + 设置 + 书签，关窗/重开/重启）

- 新隔离书籍 `a08.epub`：文字章 c0、纯图片页 c1、文字章 c2；设置行距 2.2；在 c1 纯图页保存位置；保存/关闭 PID `69936`，重启 PID `13132`。
- 已确认：
  - 保存状态 `spineIndex=1`（OPS/c1.xhtml），`preferences.lineHeight=2.2`（`a08/state-26c4….json`）。
  - 关闭 `host.finalized ok:true`、`CLOSE_ACK`。
  - 完全退出重启后打开同一书：`5-reopen-after-app-restart.png` 仍显示 c1 纯图片页；同进程关窗再开 `6-reopen-window-after-close.png` 仍恢复 c1。
- 未确认：**书签未添加**（“加书签”按钮的坐标点击没有产生 `bookmark.add`，bookmarks=[]）；测试用 1×1 PNG 在 WebView 显示为破图，不能作为图片渲染通过证据。因此 A08 记 **PARTIAL**，不写“书签同时关闭/重开通过”。

### A11 资源真正晚于 ready/恢复意图

- 本轮没有成功构造“真实资源 read 延迟跨越 ready/恢复意图”的原生/Edge 证据；R3 的图片重开证据不作为替代。A11 记 **UNTESTED**，R5 再补。

### A22 两本真实书（373 / 255 章）

- 真实用户书库只读，结果不复制书内容：
  - 《死敌怎么能变妻子呢？不行不行！》373 spine；首/中/末 `spine 0/186/372`，字号 28、书签 1、重开 `spine 372`、bookmarks1。
  - 《逃婚后被隔壁宗主追杀半个修仙界》255 spine；首/中/末 `spine 0/127/254`，字号 28、书签 1、重开 `spine 254`、bookmarks1。
- 结果 `realbooks/r4-real-books-results.json`；脚本 `tools/r4-realbooks.cjs`（浏览器，非原生）。
- **查找未通过**：输入“第”后等待 60s 仍为 `正在建立索引… 373/373` / `255/255`，`.search-result` 数量 0；脚本据此标 FAIL，因此 A22 记 **PARTIAL**（首中末/字号/书签/重开通过，真实书全文搜索未出结果）。

### A23 200 次含图切章 + 10 次换书、DOM/URL/进程趋势

- 本轮没有新的原生 200 次含图切章/进程趋势；R4 既有模块 R05（200 次图片章节切换，资源/任务保持 1）和 R3 A23 证据只能作为模块证据。A23 记 **PARTIAL**，不声称本轮已完成原生 DOM/URL/进程趋势。

### A25 干净 R4 包实际入口

- 干净解压 0.8.0 ZIP 到 `D:\Temp\lyra_r4_supp_oldhost_080`，旧宿主 b978661 启动成功；PID `73980` 打开 long、footer local 降级；PID `50636` 翻页到第 2/9 页（`compat/oldhost057-r4zip080-page2-pid.png`）。
- 包 SHA `B128…184B`，干净模块 identity 见 `compat/oldhost-b978661-r4zip080-identity.json`。A25 **PASS**（实际入口/打开/翻页）；旧宿主无法验证 R4 host 模式，按 local 降级记录。

## 6. 新发现、未修（R5 统一处理）

- D01 搜索分块漏边界：真实 Edge 三章 `CROSSBOUNDARY`，sanitize offset `8`、`65840`、`65875`；实际 chunkEnd `65844`，中间跨界匹配漏掉，UI 只找到 2 处且称扫描 3/3、未标 truncated。对应 GPT 证据 `gpt-review-r4/new-boundary-evidence/results.json` `D01_CHUNK_BOUNDARY_LOST_MATCH`。
- D02 旧查询导航仍换正文：旧结果点击、readText 挂起、输入 NEWQUERY 后旧高亮已抑制，但正文仍从 START 章换到 OLDQUERY 章（spine0→1）；R4 需要在提交文档/定位前校验，不只在 highlight 前 guard。`D02_CHANGED_QUERY_STILL_REPLACES_DOCUMENT`。
- D03 加载中关窗 ack 未保存：第 2 页 pending、下一章读取挂起（ready=false、modified=true），host.finalize 时原 controller 回 `ok:true/revision:0`，位置未写；旧 read 随后仍提交下一章。`D03_CLOSE_DURING_CHAPTER_LOAD_ACKS_UNSAVED_POSITION`；真实 Edge/local host fixture，不声称原生 C#。
- GPT 新 Edge 还指出：inline 隐藏 style 清洗前后模型不一致导致搜索 TARGET 高亮到 IDDEN/HIDDEN；SVG hidden 父+visible 子实际可见却查不到；同书共享图 prepare 后段失败撤销旧可见图。均纳入 R5，本轮不修。
- 本轮补充额外发现：真实 373/255 章书全文搜索 60s 后仍停在“正在建立索引… 373/373 / 255/255”，无结果；记为 A22 PARTIAL。

## 7. A01–A25 状态表

> 状态以 R4 补充可核对证据为准；“PASS”只代表本表对应范围内通过，不扩大 R3 覆盖。未写 R5 新修复。

| # | 状态 | R4 补充/既有证据摘要 |
|---|---|---|
| A01 | PASS | `r4-evidence/reader-smoke.out` TOC/spine；补充沿用 |
| A02 | PASS | smoke fallback toc + extra EPUB2 NCX |
| A03 | PASS | smoke 章内片段 |
| A04 | PASS | extra 身份/旧版不抢占 URL 书 |
| A05 | UNTESTED | 本轮未重做慢章/加载取消；不拿 R3 推断 |
| A06 | PASS | smoke 坏文件可恢复；本地失败路径 |
| A07 | PASS | smoke 位置恢复；A10b/c |
| A08 | PARTIAL | 补充：c1 纯图页+line2.2 关窗/应用重启恢复（截图/state）；书签未添加、PNG 破图，不写完整通过 |
| A09 | PASS | extra 同名/同内容身份 |
| A10 | PASS | A08 文字/图片页、设置落盘；长段/图片锚点沿用 R4 smoke |
| A11 | UNTESTED | 未构造资源晚于 ready/恢复意图；明确不引用 R3 替代 |
| A12 | PASS | smoke 20 次快速翻页 |
| A13 | PASS | extra 无 idle 循环 |
| A14 | PASS | smoke 选区不翻页 |
| A15 | PASS | smoke/extra 520/1400 视口 |
| A16 | PASS | smoke iframe sandbox |
| A17 | PARTIAL | 基本搜索 PASS；D01 分块边界漏匹配，真实 373/255 章书索引长时间 pending |
| A18 | PASS | smoke/extra 书签；C03 图片 ack；本地书签校验通过（A08 书签未成功另注） |
| A19 | PARTIAL | 正常双窗 A18→B32→A22 真机 PASS；pending-A18 快速顺序 A 事务 PREFERENCE_STALE、行距不落盘（实际复现） |
| A20 | PASS | C01 按书损坏隔离；失败快照 C02；书签校验 |
| A21 | PASS | 最终9EBA+真c12有书 PID57968；b978661+干净0.8 PID73980/50636；旧/旧沿用 R3 已接受基线；PDF 仅基础打开，不做全文回归 |
| A22 | PARTIAL | 373/255 首中末、字号、书签、重开通过；真实书全文搜索 60s 无结果（373/373、255/255 索引 pending） |
| A23 | PARTIAL | R4 模块 200 图片切章 + R3 旧证据；本轮无原生含图 200 次/10 次换书 DOM/URL/进程趋势 |
| A24 | FAIL | early close 1.5s 通过、pending 正常关闭通过；但 D03 加载中关窗 ack `ok:true/revision:0`、位置未写、旧 read 提交下一章 |
| A25 | PASS | 干净 0.8.0 包解包、旧宿主 b978661 启动/打开/翻页；local 降级明示 |

## 8. 补充证据文件

- 双窗：`windows/native-*`、`windows/native-fast-*`
- 写失败：`native-failure/`
- A08：`a08/`
- A22：`realbooks/r4-real-books-results.json`
- 兼容更正：`compat/finalhost-c12-*`、`compat/oldhost-b978661-r4zip080-*`
- 完整散列：`SHA256SUMS`；结构化摘要：`supplement-results.json`；冻结包/宿主身份：`supplement-results.json` 的 identity 字段。

没有修改 `r4-evidence`、候选包、R3 freeze。请按 R5 统一处理上述缺陷。
