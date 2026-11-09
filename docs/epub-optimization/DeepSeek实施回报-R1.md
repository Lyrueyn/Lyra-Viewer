# DeepSeek 实施回报 R1：EPUB 扩展优化

> 任务书：[EPUB扩展优化-DeepSeek实施任务书.md](EPUB扩展优化-DeepSeek实施任务书.md)  
> 回报日期：2026-10-26  
> 状态：**候选实现完成，尚未正式发布；请 GPT-6 验收后决定返工或进入发布轮。**  
> 本轮没有推送扩展仓库、没有覆盖用户当前安装的扩展/主程序。

---

## 1. 实际范围（M1–M4）

### M1 正确读取与加载隔离：已完成

- 拆分为无构建工具的普通网页模块：
  - `epub-book.js`：ZIP / container / OPF / manifest / spine / nav / NCX / 引用解析 / 资源 / 正文清洗
  - `paginator.js`：禁脚本 iframe 分页与文字锚点
  - `reader-state.js`：宿主协议与旧宿主降级
  - `search.js`：全文索引与查找
  - `reader.js`：生命周期、目录、书签、设置、输入调度
  - `reader.css`、`index.html`
- spine 与目录独立建模：连续阅读遵循 spine；nav/NCX 只作目录；`linear="no"` 跳过默认连续阅读；目录片段定位到章节内部。
- 统一引用解析：处理纯片段、同文件/跨文件、`../`、中文/空格编码、SVG `href`/`xlink:href`、`srcset` 第一候选；越界引用返回失效。
- 正文隔离：章节正文进入 `iframe sandbox="allow-same-origin"`，书内脚本、事件属性、表单、嵌套框架、危险链接协议被清洗或禁运行。
- 加载代次：`bookGeneration` / `navigationGeneration`；每个异步等待后检查代次。
- 加载协议：`app.ready`、`book.begin`、`book.ready`、`book.error`；旧扩展字符串/对象就绪通知仍兼容。
- 旧宿主重复投喂：URL 已打开时忽略旧脚本和 `openBook` 消息；手动选书/重试使用新 loadId；不会永久上锁。
- 已验证：A01、A03、A09、A16、A25；旧宿主/旧扩展兼容截图见证据目录。

### M2 分页与位置模型：已完成

- 分页尺寸按 iframe 实际内容盒计算，扣除外框内边距，窄窗口不再强制 360px。
- 文字锚点：`textNodePath`（沿 content 根 childNodes 的索引）+ `offsetUtf16` + `quote/prefix/suffix` + `chapterFraction`；纯图页保留元素路径。
- 字体/行距/宽度/侧栏/窗口变化：先在变化前捕获锚点，排完版再定位；长段落偏移不是只保存页号。
- 动画：`performance.now()` 时间驱动，精度阈值/连续无推进时校准并停止；连续翻页先入队，切章期间保留输入，直接跳转清空队列。
- 选择模式：默认禁选；开启后可拖选，选择/右键不翻页；减少动画设置生效。
- 已验证：A05、A07、A10b/A10c、A12、A13、A14、A15、A16。

### M3 宿主持久化：候选实现完成，协议层自测通过

- 新增 `ReaderProtocol.cs` 与 `ReaderStateStore.cs`：
  - `data/reader/epub/preferences.json` 全局偏好
  - `data/reader/epub/books/<sha256>.json` 每书位置、书签、修订号、交互资格
- `sha256:` 内容散列书身份；文件改名/复制共享状态，同内容不同名不串。
- 原子写：临时文件 + 替换；损坏状态文件保留 `.corrupt-*` 副本；读取失败按空状态恢复。
- 交互资格：宿主按真实活动窗口授予递增 token；旧窗口迟到保存返回 `STALE_INTERACTION`。
- 偏好按字段合并，带修订号；书签分页游标绑定书签修订号；容量/长度上限校验。
- 新宿主通过 `lyra-reader` JSON 协议响应 hello/state/bookmark；旧 1.1.12 宿主无响应时降级当前来源 localStorage，并显示“此主程序版本不支持跨窗口保存”。
- 主机层单元测试 H01–H10 全部通过：`candidate-evidence/host-state-results.json`。
- 实际 WebView2 候选证据：翻到第 5 页写入 `host-state-sample.json`，关闭窗口重开恢复到同一文字位置，截图 `webview2-reopen-restore.png`。

### M4 阅读能力：已完成主要功能，仍有待验收项

- 全文查找：Ctrl+F/按钮、索引进度、结果章节+摘要、结果定位、高亮（DOM mark，跳转前清除，避免污染锚点）；缓存上限 8 MiB 字符、最多 1000 条结果。
- 书签：加/删/跳转、同位置去重、摘要、重开持久化；侧栏独立 Tab。
- 阅读设置：字号、行距、正文宽度、主题/自定义背景文字色、选择模式、减少动画、侧栏显示。
- 键盘：左右/上下翻页、Ctrl+F、Ctrl+B 加书签、Esc 关闭面板；输入框不截获方向键。
- 拖入 EPUB、错误重试、窄窗收起侧栏。
- 已验证：A17、A18（本地持久化 + 宿主 bookmark.add）、A15；A18 跨窗口实际 UI 未完成实机矩阵。

### 未完成 / 未验证

以下**没有**被标为通过：

- A05：慢章/慢书 + 快速点击“最后一次意图获胜”未做真实延迟注入测试；代码已有代次保护。
- A11：晚到图片/字体晚就绪导致的重排只有代码路径，未做延迟图片测试。
- A19：store 协议层单测通过，真实 WebView2 双窗口延迟保存矩阵尚未实机执行。
- A20：损坏状态可恢复已测；磁盘写失败（只读/无权限）未模拟。
- A24：关闭正在加载/搜索/动画窗口未做 WebView2 关闭时序测试。
- A21：old host + old extension 组合未跑；new/new、new/old host、old/new host、PDF 0.3/new host、无书启动已跑。
- A22：真实书只完成一本 255 章的打开/目录验证；第二本未逐章视觉验收。
- A12 的固定三章 12 页精确数字未用本机字体/窗口做硬断言；只验证了连续输入、边界与状态一致。
- 扩展中保留了只读调试对象 `window.__lyraDebug` 便于候选测试；正式发布前应移除或仅在测试构建中启用。
- 宿主候选仍为 1.1.12 版本号，尚未决定正式版本号、未提交主程序 release、未发布任何资产。

---

## 2. 版本与文件

### EPUB 扩展

| 项 | 值 |
|---|---|
| 基线仓库提交 | `c12fe26275cdec67922cccb64d9b5f2b4c269cd2`（0.5.26） |
| 候选本地提交 | `4cf96e8e4b85332b00fcd38fb5d0575b5088664c`（0.6.0-candidate，**未推送**） |
| 清单版本 | `0.6.0` |
| 协议版本 | `lyra-reader` v1 |
| 候选包 | `docs/epub-optimization/candidates/lyra-ext-epub-0.6.0-candidate.zip` |
| 候选包 SHA-256 | `48A4C79058EB1822CB962741650CD404ED58472CB45E3B4C27098FDE2321D90E` |

运行时文件 SHA-256：

```
0D08C56E929F3A35508667B5C333A083C41B872E0F382E9F2D62EA5C6FE85A2C  epub-book.js
8721008F0203596203C7BBEE8F1DD79952C905480F41F793BE21C38F79D450D2  extension.json
2B3EFEAAFCEBA9EE94AE091F44F7355BBECF115D5AFA6FA1083BD3F7C5AAF633  index.html
ED53EE3369ED0F79FCE37305835CFCA8DF272A7916498936A6C45A4C5CF85166  paginator.js
76D12CE409A5C11FA943921FD4ABAD0E7940734D698EEAFF1F508C4D29839233  reader-state.js
16D56D1B2B88E11D9EE372D0E4C9AB65992AFD42FDC699D6BC8CE524C522F4CF  reader.css
AB44F5D29A5B587572020597CAC6B9958EEA7C04A76E0459FE5E6233016F380E  reader.js
025ADD0921F2F27DD9FB3BAE6D364AAC31EC513015016BDEBB0A8AD693C7CCB5  search.js
```

### 主程序宿主候选

```
基线：b978661b8a9ec7a9c9fee87d5d323ea1db2bc5ae（1.1.12）
修改：app/WinUIGui/ExtensionHostWindow.xaml
      app/WinUIGui/ExtensionHostWindow.xaml.cs
新增：app/WinUIGui/ReaderProtocol.cs
      app/WinUIGui/ReaderStateStore.cs
构建：dotnet build F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_epub_host_candidate
```

宿主源码 SHA-256：

```
283E3E4F682D1CA20563843E19EEDD71F3829B29CB8BD69CDF1A64A42876FF88  ExtensionHostWindow.xaml
49BF89ACE0F30BF2BC1C03D1D857860BE09544A56BE25A6015DBDC8A9F13FE64  ExtensionHostWindow.xaml.cs
50D707FE82EA43BC59C5CC10668FB7A4FF7BF8F7DFD57C27B00E1C6025CAA6F7  ReaderProtocol.cs
947CFD278BD5F1D95DA0830EAF98BE979C14DB148192E22C2458D286B0B7CCFC  ReaderStateStore.cs
```

宿主候选构建文件：

```
9DF22484A8E739A80EC12D3718D2A500A49339F4414EFDBA7AD66732457DB0BF  D:\Temp\lyra_epub_host_candidate\WinUIGui.dll
D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335  D:\Temp\lyra_epub_host_candidate\WinUIGui.exe
B0ABA54930A2518EAF726583D5DCC3EDF934E380CD7F7BE076680446C1105EB7  WinUIGui.runtimeconfig.json
```

完整测试运行目录（安装版 1.1.12 副本 + 候选宿主/扩展）：

```
D:\Temp\lyra_epub_candidate
```

该目录不是安装包；用于实际 WebView2 验收测试，包含 1.1.12 的后端/资源/引擎副本。未修改用户安装目录。

---

## 3. 设计落实位置

| 设计点 | 实现 |
|---|---|
| 正文顺序/目录分离 | `epub-book.js`：`spine`、`toc`、`tocFlat`、`findSpineIndexByPath`；`reader.js`：`navigateToSpine`、`gotoTocNode` |
| 引用解析 | `EpubBook.resolveReference`、`splitReference`、`normalizeSegments`；iframe 内链接拦截在 `paginator.js` + `reader.js` |
| 内容隔离 | `paginator.js` 的 `iframe sandbox="allow-same-origin"`；`epub-book.js` 的 sanitize 清洗 |
| 加载代次 | `reader.js` 的 `bookGeneration`、`navGeneration`、`searchGen`；旧任务不提交 |
| 文字位置 | `paginator.js` 的 `captureAnchor` / `locateAnchor` / `pageForRange`；字节单位统一 UTF-16 |
| 动态重排 | `Paginator.requestLayout`，设置变化前捕获锚点；图片 `load` 后延迟 relayout |
| 动画 | `Paginator.goToPage` 的 `performance.now()` 时间驱动与停止条件；A13 静置 rAF 增量 0 |
| 翻页输入队列 | `reader.js` 的 `turnQueue` / `processTurns`；目录/搜索/书签跳转清空 |
| 宿主存储 | `ReaderStateStore.cs`：原子写、书 locks、preferences lock、交互 token、书签游标/修订号 |
| 协议 | `ReaderProtocol.cs` + `ExtensionHostWindow.OnWebMessageReceived`；严格 type/大小/session/source 校验 |
| 旧宿主降级 | `reader-state.js` 的 hello 超时与 localStorage fallback；`reader.js` 打开完成提示“不支持跨窗口保存” |
| 查找/书签/设置 | `search.js`、`reader.js`、`reader.css`、`index.html` |

---

## 4. 验收矩阵

证据文件：`docs/epub-optimization/candidate-evidence/`。  
状态说明：**PASS** = 有可复现证据；**PARTIAL** = 部分场景通过但未覆盖整条验收；**未验证** = 没做，不能当通过。

| ID | 结果 | 证据 / 说明 |
|---|---|---|
| A01 | PASS | `smoke-results.txt` A01：toc.epub 目录 3 项，不混 landmarks；spine 3 文件；点第二节定位到 `#section-two` |
| A02 | PARTIAL | EPUB3 nav（A01）、EPUB2 NCX 嵌套（`extra-results.txt` A02b）、无目录兜底（A02a）通过；EPUB3 深层嵌套单测未单独构造 |
| A03 | PARTIAL | 章内片段链接通过（smoke A03）；脚注返回路径有实现（history），未单独构造跨章脚注用例 |
| A04 | PARTIAL | `extra-results.txt` A04b：URL 书加载后旧脚本/旧消息不接管；旧宿主三条路径的完整实机重复投喂未跑；新宿主正常路径只走网址 |
| A05 | 未验证 | 慢章 + 快速点目录的真实延迟注入未做；代码有 navigationGeneration 最后一次获胜保护 |
| A06 | PARTIAL | `smoke-results.txt` A06a：404 书显示可恢复错误，随后可打开正常书；失败重试按钮有，完整换书矩阵未跑 |
| A07 | PASS | `smoke-results.txt` A07：刷新恢复同一章/页/文字锚点 |
| A08 | PASS（实机） | `webview2-reopen-restore.png` + `host-state-sample.json`：候选 WebView2 翻到第 5 页，关闭窗口重开恢复；不是浏览器模拟 |
| A09 | PASS | `extra-results.txt` A09：相同内容不同文件名共享同一位置；不同内容会被 sha256 隔离 |
| A10 | PARTIAL | A10b 长段落偏移通过，A10c 纯图页通过；行距/宽度/侧栏/窗口全部组合未逐项跑 |
| A11 | 未验证 | 晚到图片/字体晚就绪只实现路径，未做延迟资源测试 |
| A12 | PARTIAL | `smoke-results.txt` A12 20 次快速点击通过；`perf-results.txt` 199 次连续切章通过；任务书固定 12 页三章示例未做精确页数硬断言 |
| A13 | PASS | `extra-results.txt` A13：静置 1 秒 rAF 计数 3→3，无遗留循环 |
| A14 | PARTIAL | 选择文字不翻页通过（smoke A14）；右键复制菜单/输入框方向键未单独跑 |
| A15 | PASS | `extra-results.txt` A15：520/1400 无横向裁切；`smoke-results` 760；未做 100/150/200% 缩放矩阵 |
| A16 | PASS | `smoke-results.txt` A16：事件属性未执行，sandbox 仅 allow-same-origin，图片 onerror 不触发 |
| A17 | PARTIAL | `smoke-results.txt` A17：英文查找、100 条结果、定位与高亮通过；中文/跨标签/快速改词取消未单独跑 |
| A18 | PARTIAL | 本地重开书签持久化（`extra-results` A18b）通过；宿主 bookmark.add 协议在候选 WebView2 中写入状态文件成功；宿主 UI 的书签侧栏/上限未实机矩阵 |
| A19 | PARTIAL | host state 单测 H01–H10 全部通过：stale interaction、inactive reject、prefs merge、书签分页/冲突；真实双 WebView2 窗口延迟保存未跑 |
| A20 | PARTIAL | 损坏状态恢复与备份通过（H09）；磁盘写失败未模拟 |
| A21 | PARTIAL | new/new、new ext+old 1.1.12 host、old 0.5.26 ext+new host、PDF 0.3.0+new host、无书启动均有截图；old/old 未跑 |
| A22 | PARTIAL | 真实书《逃婚后被隔壁宗主追杀半个修仙界》255 章打开/目录通过（`real-book.png`）；第二本未逐章视觉验收 |
| A23 | PASS（近似） | `perf-results.txt`：199 次切章 + 10 次换书后 iframe=1、外部 DOM 节点=307、本书资源计数=0、堆约 8.0→8.36 MB；任务要求 200 次，实际 199 次（从 0 到 199 共 200 章） |
| A24 | 未验证 | 关闭正在加载/搜索/动画窗口未做 WebView2 关闭时序测试 |
| A25 | PASS | 候选 zip 解压到空目录，通过独立 HTTP 入口打开 normal.epub：标题/15 页/目录 3 项/ready，无外部开发依赖 |

---

## 5. 性能与资源

测试机器：本机 Windows / Edge Chromium 浏览器自动化，viewport 1400×900。  
不是 WebView2 性能结论；仅用于候选扩展的浏览器层比较。

| 指标 | 结果 |
|---|---|
| 200 章合成书首屏可读 | 466 ms |
| 连续切章 199 次 | 4462 ms（平均 22 ms/次） |
| 全文查找（200 章、索引约 0.3 MB） | 355 ms，返回 1 条 |
| 静置 1 秒 rAF 增量 | 0 |
| 压力后 iframe 数 / 外部 DOM 节点 | 1 / 307 |
| 压力后本书资源登记数 | 0 |
| 10 次换书后 iframe / DOM / 堆 | 1 / 307 / ≈8.36 MB |

原始数据：`candidate-evidence/perf-results.txt`。

---

## 6. 候选产物

| 产物 | 路径 | SHA-256 / 说明 |
|---|---|---|
| EPUB 候选包 | `docs/epub-optimization/candidates/lyra-ext-epub-0.6.0-candidate.zip` | `48A4C79058EB1822CB962741650CD404ED58472CB45E3B4C27098FDE2321D90E` |
| 解压后空目录验证 | `D:\Temp\lyra_epub_candidate_build\unpacked` | A25 通过 |
| 宿主构建目录 | `D:\Temp\lyra_epub_host_candidate` | 见 `candidate-evidence/host-candidate-manifest.md` |
| 完整 WebView2 候选运行目录 | `D:\Temp\lyra_epub_candidate` | 安装版 1.1.12 副本 + 候选宿主/扩展，仅本机测试用；未发布 |

候选包只含运行时文件（HTML/CSS/JS/清单/JSZip/LICENSE/README），不含 `tests/`、测试书、截图。

---

## 7. 兼容与数据

### 已实测组合

- 新扩展 0.6.0 + 新宿主：真实 WebView2 候选运行，保存/恢复通过。
- 新扩展 + 旧 1.1.12 宿主：可阅读；显示“此主程序版本不支持跨窗口保存，仅当前窗口保存”。截图 `new-extension-old-host.png`。
- 旧 0.5.26 扩展 + 新宿主：网址打开正常，旧 `lyra-book-ready` 字符串仍被宿主接受。截图 `old-extension-new-host.png`。
- PDF 0.3.0 + 新宿主：`?file=document.pdf&name=...` 正常，PDF 的 `{type:"lyra-book-ready"}` 对象被宿主兼容；真实 PDF 页面显示。截图 `pdf-0.3-new-host.png`。
- 无书启动：扩展中心打开 EPUB 窗口，界面就绪后宿主撤掉加载层，显示选书入口。截图 `no-book-host.png`。

未测：旧扩展 + 旧宿主组合；完整矩阵待 GPT-6 验收轮补。

### 状态文件

候选测试数据目录（本地副本，不是用户安装）：

```
D:\Temp\lyra_epub_candidate\data\reader\epub\
├─ preferences.json
└─ books\<sha256>.json
```

合成测试书的 sample：`candidate-evidence/host-state-sample.json`（真实用户书内容未写入候选包/报告）。

迁移边界：

- 旧 localStorage 章节索引会映射到 spine 最近章节，但不声称恢复旧版页内位置。
- 随机域名下的旧浏览器存储不能自动汇总；新宿主固定状态目录。
- 不清理旧书库/扩展状态/模型/用户文档。

回退方式：删除候选运行目录即可；用户安装目录和扩展仓库当前版本未被覆盖。

---

## 8. 待 GPT-6 复核项

1. **A05 / A11 / A24**：需要构造真实延迟资源/关闭时序测试，当前不能判通过。
2. **A19**：需要真实双 WebView2 窗口跑 A/B 延迟保存矩阵；store 协议层已通过，但宿主 UI 矩阵未完成。
3. **A20**：需要模拟只读/写失败；损坏恢复已通过。
4. **A21**：补齐旧扩展 + 旧宿主；确认正式版本策略（宿主候选仍是 1.1.12 版本号）。
5. **A12**：是否需要按任务书固定字体/窗口构造 12 页三章精确断言。
6. **A22**：第二本真实书逐章视觉验收。
7. 正式发布前移除测试用 `window.__lyraDebug`（或仅在测试构建开启）。
8. 宿主候选代码在主仓库工作区尚未提交；正式发布轮需要确定版本号、提交、构建安装包并记录安装包 SHA；本轮未做。
9. 兼容性检测/低版本提示文案是否需要再调整（当前只在旧宿主 fallback 时提示）。
10. 扩展说明已更新为候选支持范围；GPT-6 可检查是否还有旧说明残留。

---

## 9. 本轮修改文件清单

EPUB 扩展（本地提交 `4cf96e8`，未推送）：

```
index.html           重写
reader.css           新增
reader.js            新增
epub-book.js         新增
paginator.js         新增
reader-state.js      新增
search.js            新增
extension.json       0.5.26 → 0.6.0
README.md            新增候选说明
tests/reader-smoke.cjs
tests/reader-extra.cjs
tests/reader-perf.cjs
.gitignore
```

主程序（工作区修改，未提交/未发布）：

```
app/WinUIGui/ExtensionHostWindow.xaml
app/WinUIGui/ExtensionHostWindow.xaml.cs
app/WinUIGui/ReaderProtocol.cs        新增
app/WinUIGui/ReaderStateStore.cs      新增
```

测试工程：

```
docs/epub-optimization/host-state-tests/HostStateTests.csproj
docs/epub-optimization/host-state-tests/Program.cs
```

未修改：用户安装目录、扩展仓库远端、主程序 release。

---

## 10. 结论

候选版完成了任务书中最核心的正文/目录分离、沙箱正文、文字锚点、加载代次、宿主持久化协议、查找/书签/设置与窄窗适配，并完成了多次浏览器层和部分真实 WebView2 实测。

但 A05、A11、A19（真实双窗）、A20（写失败）、A24 以及完整 A21 矩阵尚未满足“全部验收通过”，不能作为正式发布版本。请 GPT-6 按本报告第 8 节列返工/补充验收；DeepSeek 侧已准备好按问题单继续修复。
