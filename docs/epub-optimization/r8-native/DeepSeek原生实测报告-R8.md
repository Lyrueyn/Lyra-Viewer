# R8 原生实测报告（委托验收方独立复核）

委托书：`R8-原生实测委托.md`
SHA-256：`18A00E0DDBDA08E05CC0C95F61ED53FDB34FDD979103AD835D4D9123A0F772A2`

## 1. 身份与环境

- 运行根：`D:\Temp\lyra_r8_candidate`（仅此目录下的程序）
- `WinUIGui.dll`：520,192 bytes，SHA-256 `1E241EA1996A59340E4CD108D303495211BA628DF9C994D31496AA4594E06BF0`（与委托书一致）
- `extensions\epub\reader.js`：91,178 bytes，SHA-256 `F6743931B9559F6D5156471EA38601AAD0FE810F53FE8B73321587EF1809189E`（与委托书 F6743931… 一致）
- `WinUIGui.exe`：SHA-256 `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
- 未改产品代码、未改 `D:\Temp\Lyra-Extension-Epub`、宿主源码、`candidates-r8/`、注册表 WebView2 策略；未触碰任何用户自己的 WinUIGui 进程。所有写入仅 `docs/epub-optimization/r8-native/` 和隔离运行根的 `data/`、`reader_host_debug.log`。
- 测试启动时环境中没有用户的 WinUIGui；测试结束已确认本运行根 `WinUIGui` 与 `msedgewebview2` 进程均为 0。

## 2. 输入方法与截图方法（验收方卡住的部分）

**键盘没送进 WebView2 的原因**：WebView2 的网页内容在 UIA 树里位于另一个顶层窗口（标题同为 `璃鸢镜 · EPUB 阅读器`，内含 `RootWebArea`），不是主 WinUI 窗口的子元素。直接向主窗口或 WebView 宿主发键盘/`SendInput` 时，焦点不在 Chromium 内容，且宿主会判 `WINDOW_NOT_ACTIVE`，拒绝 `state.interact` 与位置保存。因此前一轮没有位置文件。

**本次可用方法**：
1. UIA 读取网页元素与 AutomationId（`pageNext`、`pagePrev`、`chapterNext`、`addBookmarkBtn`、`settingsBtn`、`range` 等）与屏幕坐标；网页按钮其实能通过 WebView2 的 UIA 树看见。
2. 用 `SetForegroundWindow` + `AttachThreadInput` 强制激活阅读器顶层窗口（宿主日志出现 `ACTIVATED active=True`），再用 DPI-aware 的 `mouse_event` **物理点击**按钮坐标；不依赖 UIA Invoke（UIA Invoke 会让窗口失去激活，宿主报 `WINDOW_NOT_ACTIVE`）。
3. 每次翻页后用磁盘位置文件 `data/reader/epub/books/<sha256>.json` 的 `position.spineIndex/pageIndex/textOffset` 判定是否真正生效；宿主日志里同时核对 `state.interact ok=True`、`state.savePosition ok=True`。
4. 截图：按阅读器顶层 HWND 调用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`，进程先 `SetProcessDpiAwareness(2)`；保存 PNG 后人工看图和 UIA 标题核对。早期未声明 DPI 时抓到 2253×1294 的裁剪图，已废弃重抓；最终图均为 3379×1941 的阅读器窗口。

## 3. A08 异常关闭（强制结束进程）— PASS

- 起始状态：A08 第 1/3 章，第 10/14 页；结束进程前位置文件 `A08-force-clean-before.json`：revision=79，`pageIndex=8`，`textOffset=8182`，`bookmarks=1`。
- 截图 `A08-force-clean-before.png`（截图取阅读器顶层 HWND 1056728；内容为阅读器第 10/14 页，lineHeight=2.3 后的页码）。
- **观察项（不影响 PASS）**：位置文件里的 `pageIndex=8` 是从 0 数、相当于第 9 页；截图实际显示第 10/14 页，锚点 quote 就落在该页的 paragraph 56 上。恢复是按 `textOffset=8182` 的文本锚点定位的，保存时的 `pageIndex` 没有按当前行距/页数重算，因此出现 1 页偏差；用户看到的前后结果一致。验收方将单独查此 pageIndex 与文字锚点不一致的原因，本条只作观察记录。
- 用 `taskkill /PID 36212 /T /F` 强制结束，日志见 `A08-force-clean-before-kill-host-debug.log`；进程 PID 36212 及 6 个 WebView2 子进程全部结束。
- 重启 PID 41092，打开 A08 后：状态仍为第 10/14 页；位置文件 `A08-force-clean-after.json`：revision=79，`pageIndex=8`，`textOffset=8182`，`bookmarks=1`。
- **结束前后截图字节完全相同**（SHA-256 均为 `100634BB858C6237EBDA8B43EC9A6507D9E336ABEE48874EBF3549A45B15DAF8`），位置文件逐字段一致。
- 证据：`screens/A08-force-clean-before.png`、`screens/A08-force-clean-after.png`、`positions/A08-force-clean-before.json`、`positions/A08-force-clean-after.json`、`metrics/A08-force-clean-before-metrics.json`、`logs/A08-force-clean-*`、`logs/A08-force-clean-*appPid.txt`。

## 4. A08 正常关闭再重开 — PASS

- 阅读器内翻到第 12/14 页（`pageIndex=11`，`textOffset=9642`），已有书签 1 条；行距已从 1.95 改为 2.3 并落盘（`preferences.json` 的 `preferences.lineHeight=2.3`）。
- 激活后**物理鼠标点击阅读器右上角关闭按钮**（不是 UIA WindowPattern，避免失去激活），宿主日志：`CLOSING`，随后 `host.finalized ok=true revision=81 closeDiagnostics=[]`，`CLOSE_ACK revision=81`。
- 重开 A08：状态仍为第 12/14 页；位置文件 `pageIndex=11`、`textOffset=9642`、`bookmarks=1`、revision=81；`preferences.lineHeight=2.3`。
- 截图：`screens/A08-normal2-before-close.png`、`screens/A08-normal2-after-reopen.png`；位置与设置：`positions/A08-normal2-after-reopen-book.json`、`positions/A08-normal2-after-reopen-preferences.json`；宿主日志 `logs/A08-normal2-close-host-debug.log`。
- 说明：第一次用 UIA WindowPattern 关窗时，宿主因窗口不活跃返回 `ok=false / STALE_INTERACTION`；该次结果只说明自动化激活问题，不作为产品结论，其截图/派生文件已删除。但**累积日志 `logs/A23-host-debug.log` 里仍保留这一条原始回执**（21:44:15.852，属于 a08-valid.epub 早期 UIA 关窗，不属于 A23），另外该日志也有若干条早期未激活尝试产生的 `state.interact ok=False / WINDOW_NOT_ACTIVE`。查看日志时请按 bookId/type 过滤；A23 成功计数只统计 A23 书且 `type=state.savePosition` 的请求。

## 5. A23 原生 200 次切章 — 切章 PASS，内存/句柄趋势 PARTIAL

- 打开 `A23 201 章`，从第 1/201 章（c0）开始，用同一物理点击 `pageNext`（每章 1 页，等同切章）连续 200 次。
- 宿主 debug 日志解析（`metrics/A23-save-sequence.json`）：A23 书的 `state.savePosition` 请求 **256 次，全部 `ok=True`**；唯一 `spineIndex` **201 个，范围 0–200**；最终位置文件实际为 **`spineIndex=200`、`pageIndex=0`、`textOffset=8`**（`OPS/c200.xhtml`），`rev=256`；UIA 状态 **第 201/201 章**，正文 `A23-200`。
- 日志口径说明：`logs/A23-host-debug.log` 是累积日志，不是 A23 单书日志。除上述 256 条 A23 成功 `state.savePosition` 外，里面还有 1 条 `state.savePosition ok=False / STALE_INTERACTION`（21:44:15.852，属于 a08-valid.epub 早期一次 UIA 关窗），以及若干条早期未激活尝试的 `state.interact ok=False / WINDOW_NOT_ACTIVE`；这些都不计入 A23 的 200 次成功切章。`metrics/A23-save-sequence.json` 只筛 A23 bookId 的 savePosition，故其 `okReplies=256` 与该日志中那 1 条 A08 失败不矛盾。
- 截图：`screens/A23-run2-start.png`（第 1/201 章，c0，图 A23-0）、`screens/A23-after-200.png`（第 201/201 章，c200，图 A23-200）；位置 `positions/A23-after-200.json`。
- 内存/句柄：由于 200 次点击+每 20 次采样的长命令在工具层被中断，**每 20 次采样表没有保存下来**；只拿到结束后一次指标：WinUIGui WS=202,686,464，Private=135,413,760，Handles=1,380；6 个 WebView2 合计 WS=407,871,488，Private=357,154,816，Handles=3,341（`metrics/A23-final-rest-metrics.json`，距最后一次落盘约 53 秒）。按用户指示没有重跑 200 次，所以 A23 的“每 20 次进程趋势”仍为 **PARTIAL/UNTESTED**，不能写成完整 PASS。
- 失败/中断原因：长循环 + PowerShell 采样命令超过本工具单次调用时长，被中止；已保留可复跑的 `tools/click_series_fast.py`、`tools/click_series.py`。

## 6. A21 无书入口 — PASS

- 从扩展中心点击 EPUB 阅读器的“打开”（不带书），阅读器窗口正常出现。
- UIA：无“加载/正在”遮罩元素；页面中央显示“打开一本 EPUB 开始阅读 / 点击按钮选择文件，或把 EPUB 直接拖进这个窗口。”；底部状态“准备就绪”；页码 `/ 0`。
- 截图 `screens/A21-no-book.png` 已人工核对为阅读器空状态，不是其他窗口。
- 日志见 `logs/final-host-debug.log`（该窗口无 book.ready，正常空状态）。

## 7. 截图核对

本次提交的 7 张截图均为 3379×1941 的阅读器窗口（不是桌面/会议软件），逐张人工看过：

| 文件 | 内容 |
|---|---|
| `A08-force-clean-before.png` / `A08-force-clean-after.png` | 强制结束前后，A08 第 10/14 页；两图 SHA-256 相同 |
| `A08-normal2-before-close.png` / `A08-normal2-after-reopen.png` | 正常关闭前后，A08 第 12/14 页，正文段落 66–71 |
| `A23-run2-start.png` | 第 1/201 章，A23-0，图 |
| `A23-after-200.png` | 第 201/201 章，A23-200，图 |
| `A21-no-book.png` | 无书入口空状态 |

## 8. 结论

- A08 异常关闭：**PASS**（位置、书签、截图一致；PID 36212→41092；另见第 3 节 pageIndex 与文字锚点差 1 页的观察项，不影响 PASS）
- A08 正常关闭再重开：**PASS**（位置 pageIndex=11/textOffset=9642、书签 1、行距 2.3；关闭回执 ok=true）
- A23 切章：**200/200 成功**，但每 20 次内存/句柄趋势 **PARTIAL**（只交末次与静置后指标；按用户指示未重跑）
- A21 无书入口：**PASS**
- 未发布、未安装、未改产品代码/用户数据；测试进程已全部退出。

证据目录：`docs/epub-optimization/r8-native/`；`SHA256SUMS` 在目录根部，路径相对 `docs/epub-optimization/`。
