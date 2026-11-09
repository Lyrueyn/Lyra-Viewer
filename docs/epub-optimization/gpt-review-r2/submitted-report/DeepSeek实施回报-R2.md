# DeepSeek 实施回报 R2：EPUB 扩展优化返工

> 依据：[GPT验收结论-R1暨DeepSeek返工任务书-R2.md](GPT验收结论-R1暨DeepSeek返工任务书-R2.md)  
> R1 回报保留：[DeepSeek实施回报-R1.md](DeepSeek实施回报-R1.md)  
> 回报日期：2026-10-27  
> 状态：**R2 候选已交回，请 GPT 复核；未发布、未覆盖用户安装。**

---

## 1. 结论摘要

R1 的五个直接故障和 F01–F14 均已处理并补了对应回归；其中 F05 的取消/跨书、F11 的原生刷新等子场景仍未完整实测，见第 7 节。

- 设置按钮重复绑定、字号 36 先排版后分页、慢文件/慢章最后意图、稳定 textOffset 锚点、重复词搜索精确定位。
- 宿主真实 Activated 状态、精确 token/owner/单调 sequence、关闭 finalize、换书冲刷、偏好 patch 与字段序号、书签游标/上限/字节预算、读取/写入失败不虚报、原子替换不退回非原子复制。
- 宿主新会话/严格 loadId/旧来源 ready/错误遮罩/计时器清理、客户端完整响应信封校验。
- 危险协议双层白名单、ZIP 空格与字面 %20、编码点段越界、SVG 片段、并发资源去重/销毁回收。
- 文件名百分号、EPUB3 目录分组、字体混淆与 DRM 区分、首次 linear=no。

**F01–F14 均完成实现并补了回归；A21 的新扩展 + 真正基线旧宿主组合也已补测通过。** 旧宿主基线从 `b978661` 的 `ExtensionHostWindow` 源码构建（不是安装目录里的新宿主副本），打开长页测试书成功，底部状态明确显示“此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口”，宿主加载层最终撤掉。old/old 组合仍未测。

---

## 2. F01–F14 逐项表

| ID | 修复位置 | 根因与设计变化 | 回归步骤 | 实际结果 | 证据 |
|---|---|---|---|---|---|
| F01 | `reader.js` init/bindSettingsUi；`reader.css` | 设置按钮在初始化被绑定两次，两次 toggle 互相抵消。删掉重复绑定，只保留 `bindSettingsUi` 一处；主按钮强调色改 `#66CCFF` | 点设置按钮→面板开；关闭→关；再点→开；连续操作不重复提交 | PASS | `r2-evidence/browser-r2-results.json` R2-F01 |
| F02 | `paginator.js` setSection/layout；`reader.js` navigateToSpine | 旧流程先加载 iframe、按默认字号 layout，再由 onContentReady 应用字号，导致页数记录的是旧排版。现在 style/selectable/reduceMotion 在 iframe 内容创建后、等待字体和图片前应用，字体/资源就绪后只做一次最终 layout | 15 页默认书切 36px；等待后检查 pageCount、工具栏页数、重新 layout 稳定性；刷新后检查锚点是否在恢复页内 | PASS：36px 后 51 页；再 layout 仍 51；工具栏 51 | R2-F02/F02b；`r2-evidence/browser-r2-results.json` |
| F03 | `reader.js` openFile/openFromUrl/openBookBuffer；`paginator.js` `_sectionToken` | 旧代码在 `file.arrayBuffer()` 之后才分配打开代次，慢文件后完成会覆盖快文件；分页器也没有章节 token。现在打开意图在读取字节/fetch 前分配，分页器每章 token 校验，旧任务不提交 | 注入慢 `File.arrayBuffer`，先选慢文件再选快文件；再选坏文件；检查标题、正文与错误遮罩 | PASS：最终显示快文件；坏文件不盖旧书、仍可读 | R2-F03 |
| F04 | `paginator.js` getTextModel/rangeAtTextOffset/captureAnchor/locateAnchor；`epub-book.js` buildTextModel | 旧锚点依赖 textNodePath，高亮 mark 会拆分文本节点；现在统一“块级换行 + 文本节点”文字模型，锚点保存/恢复 `textOffset`，高亮不改文字串联；搜索/分页共享模型 | 第 6 页捕获锚点→高亮当前范围→翻页保存→刷新；重复词搜索取第 3 条并检查 mark 文本 | PASS：高亮后重开第 7 页；重复词 mark 精确为“重复句” | R2-F04/F05 |
| F05 | `search.js`；`reader.js` applySearchResult/runSearch/closeSearch | 旧搜索按 quote 猜位置，重复词会落到第一次；取消/旧书回执可能写新书。现在结果保存节段内 offset/length，用同一文字模型重建 Range；搜索代次+bookId 隔离，新搜索取消旧任务 | 271 条重复结果中取第 3 条；检查 mark；关闭搜索/换书后不让旧结果写入 | PASS（重复定位）；取消和跨书场景代码已加，浏览器未单独构造慢索引用例 | R2-F05；`client-protocol-results.json` |
| F06 | `ReaderStateStore.cs` GrantInteraction/SavePosition；`ExtensionHostWindow` Activated；`reader.js` markInteraction/flushSave | 旧代码无条件把 Activated 当激活；只比较 token 大小，不验证 owner/精确 token/序号；定时 flush 重新申请资格。现在真实 `WindowActivationState`，Grant 只在激活时；保存要求精确 token、owner 会话、sequence 严格递增；同序号只允许幂等；定时保存只 await 已有资格 | 宿主审计 H-ACTIVE/H-TOKEN；实机双窗 A/B 翻页，A 迟到保存，B 保存后检查 owner/token；A 重新操作再检查接管 | PASS：失活 A 申请资格被 `WINDOW_NOT_ACTIVE` 拒绝；A 旧 token 保存返回 `STALE_INTERACTION`；B 位置保留；A 重新激活后 token 6 接管 | `r2-evidence/host-audit-results.json`；真实双窗见候选 `reader_host_debug.log` 与状态文件 |
| F07 | `reader.js` openBookBuffer/flushSave/host.finalize；`paginator.js` settleForSnapshot；`ExtensionHostWindow` Closing/CloseTimer | 换书前直接清 modified，旧快照 await 后读可变 bookId；关闭只 cleanup。现在换书先冲刷并冻结 bookId/位置/资格；翻页动画先 settle 再 capture；宿主 Closing 取消关闭，发 `host.finalize`，等待扩展回执或 1.5s 超时；关闭停止计时器 | 翻页后 0/30ms 立即换书，回原书验证位置；翻页后 30ms 关窗，检查 CLOSING→savePosition→FINALIZED | PASS：换书后从第 7 页变第 8 页；关窗收到 CLOSING、savePosition、FINALIZED，进程干净退出 | R2-F07a；`r2-evidence/browser-r2-results.json`；候选 `reader_host_debug.log` |
| F08 | `reader.js` applyPreferences/flushPrefs；`ReaderStateStore.SavePreferences` | 旧实现每次发全量 prefs，迟到旧窗口覆盖新值。现在 applyPreferences 计算差异字段并入 pending patch，只发变化字段；宿主按字段 sequence 合并，低序号字段跳过 | 只改字号检查 localStorage 只有 fontSize；再改行距检查两字段；宿主审计 H-PREF-ORDER 用两窗口 300/500/400 序号 | PASS：firstKeys=[fontSize]；两字段均保留；迟到 20/400 不覆盖 32/500 | R2-F08；`host-audit-results.json` H-PREF-ORDER |
| F09 | `reader-state.js` 本地分页/上限；`ReaderStateStore.ListBookmarks/AddBookmark`；`ReaderProtocol`/host response | 本地 cursor 被忽略，UI 循环读同页 12 次；本地 500 条静默截断；宿主 50×4KB 响应可超 256KiB。现在本地也按 revision:offset 游标、重复/满 500 报错；宿主按响应 UTF-8 字节预算返回较少条目和 nextCursor；客户端 ID 去重、检测游标不推进 | 本地 51 条重开；宿主 H13 51 条游标；H14 50 条长书签按字节分页；H15 第 501 条报错；客户端 H16 | PASS：本地 51 条全读回；宿主 51 无重复遗漏；50 长书签页 23 条/182188 字节；501 拒绝且保留 500 | `host-state-results.json` H13–H15；R2-F09 |
| F10 | `reader-state.js` 本地写/读/路由；`ReaderStateStore` ReadObjectSafe/AtomicWrite；`ExtensionHostWindow` `_store?` | 本地写异常被吞；所有读取异常被当空状态；Replace 失败退回 Copy。现在本地写入抛 `LOCAL_WRITE_FAILED`，读损坏/不可读抛错；宿主区分 Missing/Ok/Corrupt/Unreadable/InvalidVersion，Unreadable/InvalidVersion 抛 `STATE_UNREADABLE/STATE_VERSION_UNSUPPORTED` 不写；AtomicWrite 只 Replace/Move，失败保留旧文件、清 tmp；Store 初始化失败只下降级，不阻止窗口/PDF | H16 独占锁读失败原字节不变；H17 只读文件写入失败原字节不变且无 tmp；浏览器注入 Storage.setItem 抛错检查“未保存”；损坏 JSON 备份恢复 H09 | PASS：H16/H17 均通过；本地失败显示“位置未保存，稍后重试”且未虚报成功 | `host-state-results.json` H09/H16/H17；R2-F10 |
| F11 | `ExtensionHostWindow` 导航/消息/遮罩/计时器；`reader-state.js` `_onMessage`；`reader.js` host message | 刷新不换会话；旧 ready 不校验来源；新 ready 缺失 loadId 也接受；错误遮罩状态残留；空窗兜底计时器隐藏失败；客户端只看 requestId。现在 NavigationStarting 对 Reload 取消并重建 URL/会话/loadId；legacy ready 校验当前 source；book.ready 必须等于当前 loadId；遮罩状态函数统一；所有计时器登记/清理；客户端校验 protocol/version/type/session 完整信封 | 宿主审计 H-MISSING-LOAD/H-LEGACY-SOURCE/H-REFRESH-SESSION/H-ERROR-OVERLAY/H-EMPTY-NAV-FAIL/H-CLOSE-STRUCTURE；Node 客户端诊断发送错误信封不结束请求，合法信封才完成 | PASS（宿主审计+客户端诊断）；真实 WebView2 刷新只做了源码级，没有单独录屏 | `host-audit-results.json`；`client-protocol-results.json` |
| F12 | `epub-book.js` splitReference/classifyExternalHref/prepareSection；`reader.js` handleLink | 旧实现把所有 scheme 当外链，javascript/data/file 会传给 window.open；SVG a 未统一拦截；书名可伪造 data-lyra 属性。现在解析层只把 http/https 认作 external，危险协议 blocked；清洗时先删除书内伪造 data-lyra-*，再重建可信数据；SVG a/xlink:href 同链路；外层 handleLink 再白名单一次 | links.epub：javascript、data/file、伪造 data-lyra-external、SVG xlink javascript；允许 https；点击检查 window.open 记录 | PASS：javascript/svg 均未打开；https 明确点击才打开；伪造属性被替换成真实 https | R2-F12；`browser-r2-results.json` |
| F13 | `epub-book.js` entries/_lookupEntry/normalizeSegments/_resourceBaseUrl/releaseResources；prepareSection | ZIP 空格名与字面 %20 名互相覆盖；编码点段未按解码后判断越界；同图并发创建两个 URL；销毁后迟到读取仍登记；SVG 片段丢失；旧任务释放当前资源。现在实际 ZIP 名原样登记、解码别名只做无精确条目兜底；点段解码后再判越界；res/pending 去重，destroy 后迟到 URL 立即 revoke；资源附 fragment；并发只登记一次 | 合成书同包 `pic a.png` 与 `pic%20a.png`；引用 `%20`/`%2520`；`../%2e%2e/root.png`；SVG `view.svg#two`；同图三次引用；prepareSection 立即 destroy 后统计 create/revoke | PASS：两图字节不同且分别命中；非法路径无 src 有 missing 标记；SVG src 以 `#two` 结尾；create 3/revoke 3 | `model-resource-results.json`；R2-F13a–c |
| F14 | `reader.js` openFromUrl/首次位置/toc 渲染；`epub-book.js` 目录解析/加密检查 | name 参数被 URLSearchParams 解码后再次 decode 导致 `%` 报错；EPUB3 无链接分组被拍平；加密检查把所有 EncryptedData 当 DRM；首次从 spine 0 开始不跳 linear=no。现在 name 只依赖 URLSearchParams 一次解码；无链接 li 保留为 group 节点并渲染非按钮；只对非 IDPF 字体混淆算法拒绝，字体混淆允许阅读；首次入口用 firstReadableSpineIndex | 文件名 `100%完成.epub`；tocgroup；obfuscated/DRM 两个最小 encryption.xml；linear.epub | PASS：百分号名打开；分组保留；字体混淆打开、正文加密明确报错；首次进入 spine 1 | R2-F14a–d |

---

## 3. 保存/取消时序实例

### 3.1 双窗 A/B 真实 WebView2

本轮真实候选运行目录中打开同书两个阅读窗（A、B），A 翻页后 B 翻页，A 的延迟保存被拒绝：

```text
A session 569cf2...，B session 6221d7...
A interact token 3 → A 旧位置
B interact token 4 → B 新位置（positionSession=6221d7...，positionToken=4，pageIndex=2）
A savePosition(token=3, sequence 旧) → STALE_INTERACTION
B savePosition(token=4) → ok
A 重新点击（新 user interaction）→ token 6，保存 pageIndex=4 → ok
```

最终状态文件只保留 B 的位置，随后 A 明确操作才接管；demo 日志可在候选运行目录的 `reader_host_debug.log` 中看到 `REPLY state.savePosition ok=False err=STALE_INTERACTION`。宿主事件替身 H-ACTIVE/H-ACTIVE-TAKEOVER/H-TOKEN 也给出相同裁决。

### 3.2 关窗 pending 保存

翻页后 30ms 立即关窗，候选进程实际输出：

```text
CLOSING request=...
REPLY state.savePosition ok=True
FINALIZED
```

窗口先被 Closing 取消，扩展 `host.finalize` 冲刷冻结快照后回执，宿主最多等 1.5s 再关闭；进程干净退出，计时器清理。

### 3.3 读取/写入失败

- 独占锁住状态文件后 `state.load` 返回 `STATE_UNREADABLE`，原文件字节前后一致，未被空状态覆盖。
- 状态文件设为只读后写入返回 `WRITE_FAILED`，原文件字节保留，目录无 `.tmp-*` 残留。
- 本地 `Storage.setItem` 抛异常时界面显示“位置未保存，稍后重试”，不再是“已保存”。

---

## 4. 锚点/搜索实例

| 场景 | 目标文字/操作 | 实际可视结果 | 证据 |
|---|---|---|---|
| F02 字号 36 | 15 页书切 36px | 最终 51 页；工具栏 51；再 layout 仍 51；刷新锚点 textOffset=6 位于第 0 页 | R2-F02/F02b |
| F04 高亮后保存 | 第 6 页捕获 anchor，调用 highlightRanges 后再翻页保存 | 重开在第 7 页，未回到第 1 页；anchor 仍可 locate | R2-F04 |
| F05 重复词 | “重复句” 271 条结果，点第 3 条 | iframe 内 `.lyra-search-mark` 文本 = “重复句” | R2-F05 |
| F07 换书冲刷 | 第 7 页点下一页后 30ms 换书 | 回到原书第 8 页，quote 指向第 15 段 | R2-F07a |
| F13 资源 | 同图三次、编码路径、SVG #two | 3 个 create / destroy 后 3 个 revoke；非法路径无 src；SVG `#two` 保留 | R2-F13a–c |

未完成：晚到图片/字体晚就绪的“末页锚点”未构造受控延迟测试（F02 的 late-image 只用代码路径检查）；慢章（同书章节内容延迟返回）也未单独注入。

---

## 5. A01–A25 完整表

状态口径：PASS 有实际证据；PARTIAL 只覆盖列出的子例；未测表示没做，不能算通过。

| ID | 本轮状态 | 证据/说明 |
|---|---|---|
| A01 | PASS（浏览器） | smoke A01 目录/spine/fragment；R2 保留 |
| A02 | PARTIAL | EPUB3 nav group、NCX 嵌套、无目录兜底均通过；完整深层边界未跑 |
| A03 | PARTIAL | 章内片段通过；跨章脚注返回未构造 |
| A04 | PARTIAL | URL 后旧脚本/旧消息不接管已测；三入口完整实机重复投喂未跑；新宿主已删除重复发送代码 |
| A05 | 未测 | 慢章/慢书快速点击“最后一次意图”未做受控延迟；F03 已修 task identity |
| A06 | PARTIAL | 浏览器坏文件保留旧书、不盖错误页通过；原生同窗重试/失败流程未完整跑 |
| A07 | PARTIAL | 浏览器刷新恢复通过；非默认字号/高亮后的重开通过；原生重启矩阵未完整 |
| A08 | PASS（真实 WebView2） | 候选 PID 73692、程序集 SHA 21932...；最终候选打开恢复 pageIndex=3/9；关闭 finalize 后重开证据；见 `r2-evidence/real-host-identity.json`、`real-webview2-final-open.png` |
| A09 | PASS | 同内容改名共享位置；不同内容 sha256 隔离 |
| A10 | PASS | 36px/长段落/表情/纯图页/设置按钮均已测；行距/宽度组合仅部分 |
| A11 | 未测 | 晚到图片/字体延迟未构造；只保留 late-image 代码和 F13 资源测试 |
| A12 | PARTIAL | 20 次快速连续和 199 次连续切章通过；任务书的固定 12 页净位移精确断言未做 |
| A13 | PARTIAL | 静置 rAF 增量 0；60/144/240Hz 矩阵未做 |
| A14 | PARTIAL | 选区不翻页、设置入口已测；右键复制/输入框方向键未单跑 |
| A15 | PARTIAL | 520/760/1400 宽度无横向裁切；100/150/200% 缩放未做 |
| A16 | PARTIAL | 事件属性/script/sandbox 通过；正文无法调用宿主保存接口未做定向测试；F12 已覆盖协议边界 |
| A17 | PARTIAL | 重复词精确定位通过；取消、跨书、范围/数量未完整跑 |
| A18 | PARTIAL | 本地 51 条书签、宿主存储 H13–H16 通过；宿主 UI 书签侧栏/删除回执未完整实机 |
| A19 | PASS（核心） | 真实双窗 A/B：A 旧 token 被 STALE_INTERACTION 拒绝，B 保留，A 新交互可接管；H-ACTIVE/H-TOKEN/H-PREF 通过。A19 的完整延迟矩阵仍建议 GPT 按任务书复跑 |
| A20 | PARTIAL | 不可读/损坏/只读写/本地写失败均测；未知版本和完整只读磁盘矩阵未全部跑 |
| A21 | PARTIAL | new/new、PDF 0.3.0/新宿主、新扩展/真正基线旧宿主、旧扩展 0.5.26/新宿主、无书启动均通过；old/old 未测 |
| A22 | PARTIAL（两本真实书） | 373 spine《死敌...》与 255 spine《逃婚后...》各做首/中/末章、字号 28、书签、重开；结果见 `real-books-results.json`。真实书内的查找未做 |
| A23 | PARTIAL | 200 章合成书 199 次切章+10 次换书：iframe 1、DOM 307、资源 0、堆约 23.9MB→8.3MB；F13 图片资源 create/revoke 3/3；未做含大量图片的同页压力 |
| A24 | PARTIAL | 翻页 pending 关窗 finalize 通过；关闭正在加载/搜索/资源读取时序未完整跑 |
| A25 | PASS | 0.6.1 包空目录解压、无 lib/lib、独立 HTTP 打开 normal.epub：ready/toc15页 |

---

## 6. 真实兼容性与性能

### 6.1 候选身份五项

见 `r2-evidence/real-host-identity.json`：

- PID：73692
- 可执行文件：`D:\Temp\lyra_epub_candidate\WinUIGui.exe`
- 程序集 SHA-256：`21932E2C...`
- 实际扩展目录：`...\extensions\epub`，版本 0.6.1
- 实际状态路径：`...\data\reader\epub`

### 6.2 兼容矩阵（本轮实测）

| 组合 | 结果 |
|---|---|
| 新扩展 0.6.1 + 新宿主候选 | PASS：真实 WebView2 打开/翻页/关闭 finalize/重开；PDF 3.0 页、无书启动均通过 |
| PDF 0.3.0 + 新宿主 | PASS：PDF 页面真实显示，`{type:"lyra-book-ready"}` 被宿主接受；截图 `pdf-0.3-r2-host.png` |
| 新扩展 + 旧 1.1.12 宿主 | PASS（本轮补测）：从 `b978661` 的旧 `ExtensionHostWindow` 源码构建真正的旧宿主，打开长页测试书成功；底部明确显示“此主程序版本不支持跨窗口保存；阅读位置仅保存在当前窗口”，不是假的“自动保存”；截图 `new-extension-oldhost-real.png`。安装目录副本不作为旧宿主证据。 |
| 旧 EPUB 0.5.26 + 新宿主 | PASS（本轮补测）：旧 0.5.26 扩展目录接入新候选宿主，打开长页测试书正常，宿主加载层撤掉；截图 `old-extension-newhost-r2.png` |
| old/old | 未测 |
| 无书启动 | PASS：app.ready 后加载层撤掉，显示选书入口 |

### 6.3 性能（最终 R2 扩展，浏览器自动化）

- 200 章合成书首屏：500ms；199 次连续切章：5030ms，平均 25ms；查找：371ms。
- 压力后 iframe=1、外部 DOM 节点=307、资源登记=0；10 次换书后 iframe=1、DOM=307、堆约 8.3MB。
- 静态图片/资源：F13 create/revoke 3/3；没有大量图片同页压力结论。

原始数据：`r2-evidence/perf-results.txt`、`model-resource-results.txt`。

---

## 7. 未完成与需要 GPT 复核

1. **A21 old/old**：只缺旧扩展 + 旧宿主组合；new/new、new/old host、old ext/new host、PDF、无书均已在 R2 实测。
2. **A05/A11**：慢章/慢书、晚到图片/字体就绪的受控延迟未做；不能把 F03 的身份修复当作 A05 已通过。
3. **A12/A13/A15/A17/A18** 的完整矩阵（固定页精确断言、不同刷新率、缩放、搜索取消/跨书、宿主书签 UI）仍未全部跑。
4. **A22 查找**未在真实书内跑；两本书的首/中/末章、字号、书签、重开已通过。
5. **A23 含图压力**未做；F13 只覆盖资源所有权和 3 张图。
6. **A24** 关闭正在加载/搜索/资源读取未完整跑；本轮只证明 pending 翻页关窗。
7. 正式发布前需移除测试模式入口 `window.__LYRA_TEST_MODE__` 的调用（正式包默认不暴露 `__lyraDebug`，测试脚本自行注入；本轮已改为默认关闭）。
8. 宿主候选版本号仍为 1.1.12，尚未确定 release 版本、提交主程序 release、生成安装包；本轮未发布。
9. old/old 组合和旧扩展完整回归仍需 GPT 按正式发布轮再核；新扩展/旧宿主降级提示已在真实旧宿主上确认。

---

## 8. 交付清单与命令

见 [r2-manifest.md](r2-evidence/r2-manifest.md)。摘要：

- 候选包：[lyra-ext-epub-0.6.1-candidate.zip](candidates-r2/lyra-ext-epub-0.6.1-candidate.zip)
  - SHA-256：`65CF0784AD4A006BA2444D1A5B08EF2C354B116E71325DFCC5E9A2C194170063`
  - 本地提交：`c96c912ea1ab1e9a7a311620663ea4f6634a3d18`（未推送）
- 宿主构建：`D:\Temp\lyra_epub_host_r2`
- 候选运行目录：`D:\Temp\lyra_epub_candidate`
- 宿主程序集 SHA：`21932E2C246BF6152BA9C42834F60654A7A5AE239151B645F0EE8F14F17A30DA`
- 构建：`dotnet build app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_epub_host_r2`

---

## 9. 修改文件

扩展（本地提交 `c96c912e`，未推送）：

```
index.html reader.css reader.js epub-book.js paginator.js reader-state.js search.js
extension.json 0.6.0 → 0.6.1
tests/reader-smoke.cjs tests/reader-extra.cjs tests/reader-perf.cjs
tests/r2-checks.cjs tests/r2-model.cjs tests/r2-client.cjs tests/r2-realbooks.cjs
README.md .gitignore
```

主程序（工作区修改，未提交/未发布）：

```
app/WinUIGui/ExtensionHostWindow.xaml
app/WinUIGui/ExtensionHostWindow.xaml.cs
app/WinUIGui/ReaderProtocol.cs       新增
app/WinUIGui/ReaderStateStore.cs     新增
```

测试工程：

```
docs/epub-optimization/host-state-tests/
docs/epub-optimization/r2-host-audit/
```

未修改用户安装目录、未推送远端、未生成正式 release。
