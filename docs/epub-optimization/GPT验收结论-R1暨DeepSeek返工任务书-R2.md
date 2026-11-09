# EPUB 优化 R1 验收结论暨 DeepSeek R2 返工任务书

**结论：R1 未通过验收，进入 R2 返工；当前 0.6.0 候选不具备发布条件。**

这轮已有可保留的成果：模块拆分、正文顺序与目录分离、基本片段跳转、正文脚本隔离、SHA-256 书籍身份、宿主持久化接口、搜索和书签的主体界面。独立重跑原浏览器用例与 H01–H10 均通过。但进一步复测发现了普通使用即可触发的功能故障，以及受控延迟、存储失败时的数据完整性问题。因此，下一轮需要修实现并补强验收，不能只补截图或将原测试再跑一遍。

最直接的五个例子：

- 点击设置按钮，面板仍关闭；同一按钮被绑定了两次切换处理。
- 36 号字切章后，正文实际需要 37 页，阅读器却只记录 16 页；重开后原文字也不在恢复页内。
- 旧章延迟完成后，把新章第 6 页拉回第 1 页；先选慢书、后选快书，最终反而显示先选的旧书。
- 本地保存了 51 条书签，界面显示 600 项，只有 50 个不同 ID，第 51 条不可达；写入失败时仍显示“已保存”。
- 宿主业务代码隔离测试中，失活窗口重新获得保存资格并覆盖新窗口的位置；暂时不可读的有效状态文件还可能被当成空状态写回，丢掉原位置和书签。

本文件是交给 DeepSeek 的下一轮执行依据。继续遵守[原实施任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md>)，本文件补充已查明的问题、修正约束和验收方法。原任务书 A01–A25 的含义不变。

## 1. 验收对象与证据边界

验收输入为[DeepSeek 实施回报 R1](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/DeepSeek实施回报-R1.md>)及其指向的候选实现。

| 对象 | 本次确认 |
|---|---|
| EPUB 基线 | 0.5.26，提交 `c12fe26275cdec67922cccb64d9b5f2b4c269cd2` |
| R1 扩展候选 | 0.6.0，提交 `4cf96e8e4b85332b00fcd38fb5d0575b5088664c`；验收时未推送 |
| [候选压缩包](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidates/lyra-ext-epub-0.6.0-candidate.zip>) | SHA-256：`48a4c79058eb1822cb962741650cd404ed58472cb45e3b4c27098fde2321d90e` |
| 源码与包内容 | 运行文件字节一致；宿主四个源文件散列与回报一致，详见[身份核对记录](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/identity.json>) |
| 宿主候选二进制 | 构建目录的 DLL、EXE、运行配置，以及完整运行目录的 DLL，SHA-256 均与[候选清单](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidate-evidence/host-candidate-manifest.md>)一致；这是文件核对，不等同于证明实际运行进程身份 |
| 打包小问题 | 包内多出一份 `lib/lib/jszip.min.js`；正确位置的库也存在，故不是“缺库不能打开” |

本轮由 GPT 进行审查和诊断，没有修改产品实现、原回报、原测试或原候选证据，没有覆盖现用安装或发布。浏览器测试运行候选压缩包的独立解压副本；宿主测试只使用隔离合成状态。

证据分三种，结论中必须保留此区别：

1. **浏览器实测**：Playwright 驱动本机 Edge，实际加载候选 HTML/JS，操作界面、读取正文位置。部分用例注入可控延迟或故障以稳定制造时序。
2. **模块 / 宿主业务代码诊断**：Node 执行原 JS；C# 工程直接链接原宿主、协议、存储源文件，使用 WinUI/WebView2 事件替身，通过原消息入口调用真实磁盘存储。后者不是原生 WebView2 窗口集成测试。
3. **源码确认 / 待补实测**：能从实现直接判断的遗漏明确写出；原生关闭时序、完整兼容矩阵等，不能用模拟结果冒充实机通过。

本轮未重新完成所有真实桌面组合，因为已经有足以否决 R1 的确定故障。R2 修复后仍须补齐原任务书要求的真实宿主证据。

## 2. 证据索引与原测试的问题

| 证据 | 内容 |
|---|---|
| [浏览器复测结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/browser-evidence/review-results.json>) | R01–R12，设置、字号、切章延迟、搜索、书签、文件名、偏好、换书保存、写入失败 |
| [边界复测结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/boundary-evidence/boundary-results.json>) | R13–R17，文件读取先后、旧搜索写入新书界面、外链协议转交、关闭搜索 |
| [高亮与保存位置结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/anchor-evidence/anchor-result.json>) | R18，调用候选高亮方法后正常翻页、保存、重开，第 8 页回到第 1 页 |
| [模块诊断结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/model-results.json>) | M01–M07，资源漏收、搜索上限、取消、编码路径 |
| [宿主与保存专项报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/host-audit/宿主保存链路独立验收-R1.md>) | HA01–HA13，完整源码位置、方法、复现、边界与复跑命令 |
| [宿主诊断数据](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/host-audit/runs/r1/host-results.json>) / [客户端诊断数据](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/host-audit/client-results.json>) | H-* / J-* 原始结果，含通过的对照组 |
| [原 smoke 隔离重跑](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/candidate/tests/artifacts/smoke-results.json>) / [原 extra 隔离重跑](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/candidate/tests/artifacts/extra-results.json>) | 原有浏览器断言全部通过 |
| [原 H01–H10 隔离重跑](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/host-audit/runs/original-tests/host-state-results.json>) | 原有存储断言全部通过 |
| [字号分页错误截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/browser-evidence/font-layout-stale.png>) | 36px 正文，工具栏却仍显示总共 16 页；实际宽度测量见 R02 |

**探针异常不算产品缺陷，也不算通过。** R06 的初次旧搜索探针没有形成明确区分，不用它下结论；后续 R14 使用新书绝不含旧词的样本，确认旧结果进入新书界面。R15 在等待搜索高亮的前置条件处超时，未据此宣布锚点故障；R18 独立调用候选自身高亮函数构造有效高亮，再通过真实页码输入和刷新确认了保存位置错误。

原测试存在以下覆盖缺口，R2 要直接修正：

- [smoke 测试:150](<D:/Temp/Lyra-Extension-Epub/tests/reader-smoke.cjs#L150>) 的“连续 20 次翻页”只检查页码不越界，不检查实际应到哪一章哪一页；查找只检查有结果、有高亮，不检查第几个命中是否进入可视区；书签跳转断言也没有核对目标位置。
- 原 smoke 将字号重排标成 A05；原任务书 A05 实际要求慢章/慢书与最后一次用户意图。编号必须恢复一致。
- 设置和选择模式部分直接改状态，绕过了真实按钮，因而没有发现设置面板打不开。
- H04 手工向 Store 传 `windowActive=false`，没有经过真实激活事件处理，遗漏了 Deactivated 仍被记成活动的问题。
- [性能测试:59](<D:/Temp/Lyra-Extension-Epub/tests/reader-perf.cjs#L59>) 吞掉切章等待超时；[第 87 行](<D:/Temp/Lyra-Extension-Epub/tests/reader-perf.cjs#L87>) 的十次换书通过整个页面重新导航实现。纯文字书、空资源计数和重建页面不能证明同一阅读器内反复换插图书没有资源泄漏。

## 3. R2 问题单与通过条件

P1：核心功能、数据完整性或关键隔离规则故障，优先修复。P2：确定的兼容、协议或边界错误，同样要在本轮完整验收前处理。打包冗余等收尾项放在第 5 节。以下 F01–F14 是 R2 回报的固定问题编号。

### F01 · P1 · 设置面板打不开

**证据：** R01 实际点击后 `actualOpen=false`。[绑定设置界面](<D:/Temp/Lyra-Extension-Epub/reader.js#L505>)与[初始化末尾](<D:/Temp/Lyra-Extension-Epub/reader.js#L1038>)同时绑定 toggle，一次点击开后又关。

**修正：** 同一行为只绑定一次；检查页码输入等多个事件并发触发是否重复提交同一用户动作。界面重点色按原任务书使用 `#66CCFF`；目前[主按钮仍为粉色](<D:/Temp/Lyra-Extension-Epub/reader.css#L23>)，作为界面收尾一并修正。

**通过：** 点击打开、再次点击关闭、关闭按钮、Esc 都有效；从真实设置面板操作字号、行距、宽度、主题、选择模式、减少动画，分别验证效果和保存。正式用户流程测试不得通过直接添加面板 class 绕过按钮。

### F02 · P1 · 先分页后应用设置，造成正文页数不足及恢复错位

**证据：** R02 从 36px 的第一章切到第二章，实际正文需要 37 页，候选 `pageCount=16`；R03 保存第 8 页后刷新，恢复到第 4 页，原锚点在可视区右侧数千像素外。不是单纯“页号不同”。

**位置：** [分页器 setSection:208](<D:/Temp/Lyra-Extension-Epub/paginator.js#L208>)先 layout/locate/goToPage，最后才调用 onContentReady；[阅读器回调:997](<D:/Temp/Lyra-Extension-Epub/reader.js#L997>)此时才 applyStyle。[切章流程:675](<D:/Temp/Lyra-Extension-Epub/reader.js#L675>)还有一次排版完成后的样式应用。

**修正：** 在新文档中先应用最终阅读设置，再等待相关字体/资源，再计算最终布局，最后执行恢复锚点、片段或末页意图。把这套顺序放到统一入口，避免普通切章与恢复走不同规则。晚到图片重排要使用此前已确认的可见内容锚点，不能等图片改变排版后才采集所谓“旧锚点”。

**通过：** 16/22/36 号字与不同行距、宽度下：切章后记录页数与独立正文几何测量一致，章末文字可达；同源刷新、跨窗口重开都包含原可见锚点。另补 A11 慢图、字体晚到、纯图页、从上一章进入末页，验证不漂移、不被旧页数截断。

### F03 · P1 · 最后一次用户意图没有贯穿整个异步过程

**证据：** R04 延迟旧章节资源等待 1400ms，新章节已经进入第 6 页，旧等待完成后新章节被重置到第 1 页。R13 延迟第一次文件读取，用户后选 FRESH 已成功显示，最后却被先选的 OLD 接管。R11 还确认迟到的旧书书签列表进入当前书。

**位置：** [setSection:225](<D:/Temp/Lyra-Extension-Epub/paginator.js#L225>)多次 await 后只检查 destroyed，没有检查章节任务身份；外层[切章代次检查](<D:/Temp/Lyra-Extension-Epub/reader.js#L645>)发生在共享分页器已经被修改之后。[openFile:821](<D:/Temp/Lyra-Extension-Epub/reader.js#L821>)等 arrayBuffer 完成后才进入 openBookBuffer 建立书籍代次。[书签列表:308](<D:/Temp/Lyra-Extension-Epub/reader.js#L308>)也没有把请求绑定到固定书籍代次。

**修正：** 用户选文件、拖入、URL 加载等入口一接受新意图就分配身份；文件读取、解压、加载状态、准备章节、iframe load、资源等待、分页、列表、回执全过程携带身份。每个 await 后在修改共享对象之前校验。分页器也要有自己的章节/文档身份和取消逻辑，不能仅靠阅读器外层最后检查。提交新书要完整切换该书状态；失败回退须恢复完整旧书界面与状态。旧任务只释放它自己的资源，不得处理当前书。

**通过：** 慢 A→快 B、A→B→A、连续目录跳转、旧 iframe load/图片回调迟到、慢 arrayBuffer/fetch/状态读取全部稳定由最后意图获胜；正文、目录、页码、书签、搜索和持久化 bookId 一致。每个用例核对目标正文内容，不只核对 state 中的索引。

### F04 · P1 · 搜索高亮改变 DOM 后，文字锚点不能可靠恢复

**证据：** R18 用候选自身的 highlightRanges 高亮有效范围后，翻到第 8 页保存，刷新回到第 1 页。保存路径含 `[1,2]`，它描述的是被高亮拆分后的文本节点；重新打开的正文结构已不同。

**位置：** [captureAnchor:351](<D:/Temp/Lyra-Extension-Epub/paginator.js#L351>)直接记录当前 DOM；[locateAnchor:403](<D:/Temp/Lyra-Extension-Epub/paginator.js#L403>)对节点路径缺少上下文验证，回退偏向首个相同文本；[highlightRanges:545](<D:/Temp/Lyra-Extension-Epub/paginator.js#L545>)拆节点，[clearHighlights:575](<D:/Temp/Lyra-Extension-Epub/paginator.js#L575>)只去包装，不恢复统一节点结构。

**修正：** 保存位置基于稳定、经过统一清洗的正文坐标；高亮不得改变持久化坐标含义。可以使用不修改原正文的高亮方案，也可以维护显式映射。节点路径成功解析仍要用 quote/prefix/suffix 校验；重复句回退必须结合上下文、章内偏移/附近位置，不能直接选第一次出现。清除高亮要恢复可预测结构。

**通过：** 长段落中相同句子重复多页，分别在搜索前、搜索后、清除高亮后保存位置和书签；改字号、切章再回、刷新、宿主重开均恢复到原命中附近，原锚点位于当前页内。加入跨标签文本与 emoji 的 UTF-16 偏移验证。

### F05 · P1/P2 · 搜索定位、取消及上限需要共同修正

**证据：** R05 点击第二个重复命中，仍停在第一处；跨行内标签 `甲<span>乙</span>丙` 有结果但无有效定位/高亮。R14 新书 FRESH 已打开，旧书的 OLDONLY 结果仍进入列表；R17 关闭搜索后 cancel 调用为 0，旧扫描继续完成。M03 跨章 1001 条只报 1000 且未标截断；M04 首章超过缓存上限使后章完全不扫描；M05/M06 分别确认热缓存取消失效、冷索引取消后新查询也失败。

**位置：** [结果定位:114](<D:/Temp/Lyra-Extension-Epub/search.js#L114>)没有使用结果 offset/length，改为查找带前后文的 quote；[索引/取消:37](<D:/Temp/Lyra-Extension-Epub/search.js#L37>)与[查询:75](<D:/Temp/Lyra-Extension-Epub/search.js#L75>)共用代次且捕获时机错误；[关闭搜索:429](<D:/Temp/Lyra-Extension-Epub/reader.js#L429>)及[换书:790](<D:/Temp/Lyra-Extension-Epub/reader.js#L790>)未真正取消。索引提取与[正文清洗](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L472>)规则也不一致。

**修正：**

1. 从同一份清洗后的可见文本模型建立“规范化 UTF-16 偏移→DOM Range”的映射，以命中 offset/length 定位，只高亮关键词实际范围；统一块间空白和隐藏/被删除元素的处理。
2. 分别管理书籍、索引构建、当前查询身份。输入改词、关闭、换书立即淘汰旧查询与输入防抖任务；取消后新查询可以正常开始，不能复用注定失败的旧 Promise。
3. 索引和扫描均分块让出事件循环，长单章也有取消点。缓存限额不能等同于停止扫描后面的书。有限结果展示至少能判定第 1001 个命中；若有未扫描范围，零结果也必须明确说明，不能宣称全书没有结果。限制单位写清楚，当前常量是 UTF-16 单位，不是实际占用 8MiB。

**通过：** 重复命中第二处、短中文跨标签、英文多空白、跨块、emoji、隐藏内容、精确高亮长度；冷/热取消、连续改词、关闭后重新查找、索引中换书；1000/1001 跨章、超限单章与后章唯一命中。核对可视范围和真实文字，不能以“有 mark 节点”为通过标准。

### F06 · P1 · 多窗口保存资格与位置序号未正确裁决

**证据：** H-ACTIVE / HA01：A 失活后仍取得更大 token，覆盖 B 已保存位置。H-TOKEN / HA02：使用另一个会话的 token、sequence 从 20 倒退至 2、使用未授予的 token=101，均成功落盘。

**位置：** [宿主激活事件:61](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ExtensionHostWindow.xaml.cs#L61>)无条件写活动状态；[SavePosition:169](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderStateStore.cs#L169>)仅拒绝小于当前 token，未验证 owner、等值和已接受序号。客户端[flushSave:244](<D:/Temp/Lyra-Extension-Epub/reader.js#L244>)还会在定时保存时重新申请交互资格。

**修正：** 正确处理 Activated 的实际激活/失活状态；保存资格来自有效的新用户交互，定时落盘、旧任务回执、关闭冲刷不能被当成新交互来抢占。服务端验证书籍、当前有效会话、token 所有者、精确 token、单调序号；处理重复请求应幂等，不能覆盖成更旧的位置。

**通过：** 先在原消息入口重跑 H-ACTIVE/H-TOKEN，再在两个真实 WebView2 窗口执行原 A19 时序。延迟项同时覆盖 interact 和 savePosition；B 保存后，A 迟到或关闭不能回退进度；A 真正重新激活并明确操作后能够接管。回报保存前后状态文件和 token/sequence，不用原 H04 单测替代。

### F07 · P1 · 换书与关闭前的最后快照缺少完整协调

**证据：** R10 翻页后在防抖保存前立即换书，回到原书已丢掉刚才位置，原保存为空。[openBookBuffer:738](<D:/Temp/Lyra-Extension-Epub/reader.js#L738>)直接清 modified；[flushSave:249](<D:/Temp/Lyra-Extension-Epub/reader.js#L249>)在 await 之后重新读取可变的当前 bookId，存在把旧快照提交到新书的路径。宿主关闭只做 cleanup；beforeunload/pagehide 仅启动异步保存，没有等待其完成。

**修正：** 每份保存快照冻结 bookId、会话/书籍身份、位置、资格与序号，之后不再读取可变全局身份。换书前提交原书的有效待保存快照，取消或隔离后续旧任务；保存完成后的 UI 回执也校验当前身份。关闭使用原任务书要求的宿主有限等待/最后已确认快照机制，涵盖位置和尚未提交的设置，不在销毁中重新争取交互资格。

**通过：** 翻页或改设置后分别在 0/50/150ms 换书/关窗；保存等待期间换书，证明每份磁盘状态只含对应书的坐标。真实 WebView2 中关闭正在读取、索引、动画的窗口，再打开检验。H-CLOSE-STRUCTURE 只证明关闭协调和计时器管理缺口，不能当作原生关窗必丢的实测。

### F08 · P1 · 偏好保存发送完整旧快照，迟到更新覆盖新设置

**证据：** R09 只改字号，却提交了所有偏好字段。H-PREF-ORDER / HA04 中 B 较新字号 32 被 A 迟到的 18 覆盖。顺序提交不同字段的 Store 合并对照组正常，不代表真实客户端完整快照安全。

**位置：** [偏好防抖:203](<D:/Temp/Lyra-Extension-Epub/reader.js#L203>)发送 state.prefs；[宿主转发](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ExtensionHostWindow.xaml.cs#L283>)和[偏好存储:245](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderStateStore.cs#L245>)无新旧交互裁决。

**修正：** 收集本次实际修改字段，合并为 pending patch，仅发这些字段；偏好使用全局修订/有效交互规则处理同字段冲突。全局偏好文件应由共享串行机制保护，不能让每个窗口的新 Store 各持互不相干的锁。不要让闲置窗口或关窗重写全量旧设置。

**通过：** 不同书的两窗分别改字号、主题，两者保留；同字段 A 先改但延迟提交，B 后改先落盘，A 迟到不覆盖；A 后续新操作可生效。H-STORE-LOCKS 的两 Store 工作线程并发结果只作为锁设计证据，不能说已证明当前同 UI 线程的正常双窗必然并发丢字段。

### F09 · P1/P2 · 书签分页、数量与消息字节限制不一致

**证据：** R07 / J-LOCAL-CURSOR：51 条存储数据被 UI 读成 12 轮相同的前 50 条，共 600 项，最后一条不可达。J-LOCAL-CAP：501 次本地添加全部报告成功，最早书签却被静默删除。H-PAGE-BYTES：50 条每条都合法的书签，响应序列化后为 379,568 字节，超过 262,144 字节上限。R11 另有迟到列表串书。

**位置：** [本地分页:202](<D:/Temp/Lyra-Extension-Epub/reader-state.js#L202>)忽略 cursor；[本地写入:210](<D:/Temp/Lyra-Extension-Epub/reader-state.js#L210>)及后续本地保存截取末尾 500 条；[宿主列表:284](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderStateStore.cs#L284>)仅按条数分批，[响应序列化](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderProtocol.cs#L60>)没有限制最终字节数。

**修正：** 本地与宿主执行同一数量/单项/列表规则；游标必须推进，客户端检测重复游标并按 ID 去重，分页任务绑定固定书籍。满 500 条新增明确报错，保留现有数据。响应按完整 JSON 的实际 UTF-8 字节计量，含协议封装和转义扩容；必要时减少单页条数，返回可继续的游标。列表失败须可见，不能吞异常后伪装成空书签。

**通过：** 0/1/50/51/499/500/501 条，本地与宿主各跑；中文及接近单项字节上限样本完整分页读回，无重复无遗漏；超限不丢旧签；换书期间的列表/添加/删除回执不修改当前另一书；改字号、重开后的书签定位与 F04 联测。

### F10 · P1 · 存储失败处理会虚报成功，甚至覆盖有效旧数据

**证据：** R12 / J-LOCAL-WRITE-FAIL：localStorage 抛写入错误，仍显示已保存，下一次读取为空。H-READ-FAIL-DATA-LOSS：有效状态暂时拒绝读取但允许替换时，interact 返回成功，原位置变 null、书签数变 0，未留下 corrupt 备份。J-READ-FALLBACK-MODE：提示已退回 local，后续仍向失败的 host 保存。

**位置：** [本地写入辅助函数:235](<D:/Temp/Lyra-Extension-Epub/reader-state.js#L235>)吞异常；[ReadObject:65](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderStateStore.cs#L65>)把所有读取异常当空状态；[AtomicWrite:83](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ReaderStateStore.cs#L83>)在 Replace 失败时退回直接覆盖复制，不能兑现完整原子写入保证；[加载降级:165](<D:/Temp/Lyra-Extension-Epub/reader-state.js#L165>)仅改变返回值与提示，没有改变实际保存后端。

**修正：** 区分不存在、有效、格式损坏、暂时不可读、未知版本；读取失败不能授权空状态覆盖有效文件。损坏文件安全保留后再按明确策略恢复；未知格式不可静默清空。成功提示必须以实际提交成功为依据；失败保留内存中的待保存内容、明确提示并可重试。持久化模式与实际路由一致；故障解除时不得把陈旧本地副本自动覆盖宿主。安全替换失败时保留旧文件并报告错误，不能把非原子复制称为原子写。存储初始化失败也不得阻止继续阅读或影响 PDF 打开。

**通过：** 配额满、存储禁用、文件只读、暂时拒读但允许替换、目录不可写、损坏 JSON、未知版本、替换中断；验证原文件字节/备份、错误提示、恢复后的重试。普通只读文件返回 HOST_ERROR 且保留原字节在本轮已通过，需保留该对照，避免修复时退步。

### F11 · P1/P2 · 宿主会话、就绪遮罩和关闭计时器存在遗漏

**证据：** H-REFRESH-SESSION 普通导航完成后会话不换；H-LEGACY-SOURCE 的旧 ready 接受外来来源；H-MISSING-LOAD 的新 ready 接受缺失 loadId。H-ERROR-OVERLAY：app.ready→book.error→新 begin/ready 后，正文已 ready，旧错误遮罩仍可见。H-EMPTY-NAV-FAIL 中空窗计时器会把导航失败遮罩藏掉；H-CLOSE-STRUCTURE 中关闭后仍有一个计时器运行。J-RESPONSE-VALIDATION 接受了 requestId 相同但协议/版本/type/session 错误的响应。

**位置：** [宿主导航与消息处理](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ExtensionHostWindow.xaml.cs#L65>)、[遮罩状态:357](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ExtensionHostWindow.xaml.cs#L357>)、[关闭清理:394](<F:/DSH/LocalMediaPlayer-Workspace/app/WinUIGui/ExtensionHostWindow.xaml.cs#L394>)；[客户端响应处理:51](<D:/Temp/Lyra-Extension-Epub/reader-state.js#L51>)。

**修正：** 顶层导航/刷新建立新会话并取消旧 pending；旧兼容 ready 也限制在当前来源/导航，新协议 begin/ready/error 严格核对有效 loadId。客户端校验完整响应信封后再结束请求。加载、可读、失败、取消统一管理遮罩和计时器，显示错误后不能因隐藏标志残留而无法恢复；关闭停止所有登记计时器、监听和任务。

**通过：** 同窗失败→选好书、失败→重试、空窗导航失败、刷新时旧回执迟到、不同 loadId 的 ready/error 交错、错误来源旧 ready、错误响应信封；真实宿主再跑有书/无书启动与新旧兼容组合。上述错误信封测试是定向协议校验，不宣称普通 UUID 会自然碰撞。

### F12 · P1 · 危险协议被转交外层，内容边界清洗不完整

**证据：** R16 无害测试链接 `javascript:window.__benignProbe=1` 经正文清洗后被保留为 data-lyra-external，点击后传入外层 window.open。测试拦截了 window.open 并记录参数，**没有执行该 URL，也没有证明脚本执行或 sandbox 逃逸**。这已足以否定 R1“危险链接协议被清洗”的陈述。

**位置：** [协议分类:43](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L43>)将任意 scheme 都当外链，[链接清洗:485](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L485>)保存它，[外层打开:893](<D:/Temp/Lyra-Extension-Epub/reader.js#L893>)没有白名单。SVG 的传统 a[xlink:href] 也未统一进入此拦截链路。

**修正：** 清洗层与外层打开入口两处执行明确协议白名单；危险协议不转交，正常外链只在明确点击时打开。清除书内自行伪造的内部控制属性，再建立可信导航数据；原书直接带 data-lyra-external 也不能绕过外层校验。覆盖普通 HTML、SVG href/xlink:href、同章片段和包内资源。

**通过：** 无害 javascript/data/file 等链接不触发外部打开或主导航；允许的 http(s) 按明确点击打开；SVG 链接不能使章节框架脱离阅读器控制。保留原来已经通过的 script/onerror/嵌套框架隔离用例，并检测正文无法调用宿主保存接口。

### F13 · P1/P2 · 资源所有权及包内路径解析仍有边界错误

**证据：** M01：图片读取挂起后 destroy，迟到返回仍创建并登记 URL，未撤销。M02：同一路径并发读取生成两个 URL，Map 只记录后一个，destroy 只撤销后一个。M07：`../%2e%2e/out.png` 得到包根外形态的 `../out.png`，未判非法。源码及独立模块检查还确认 ZIP 空格名称与字面 `%20` 名称相互覆盖，以及 SVG 图片引用片段在替换 Blob URL 时丢失。

**位置：** [资源创建:441](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L441>)、[资源释放:569](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L569>)、[ZIP 索引:143](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L143>)、[路径归一化:27](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L27>)、[图片地址改写:504](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L504>)。

**修正：** 同路径正在读取的资源去重；资源属于具体书/章节任务，异步返回后核对有效性，过期或销毁时回收迟到创建，正在使用的资源不能被旧任务释放。ZIP 实际条目名称原样登记，对引用只解码一次，再处理点段并拒绝越界；容错别名不能覆盖真实条目。资源缓存以文件路径共享，但实际引用保留安全编码后的 SVG 片段。

**通过：** 同图并发、读取中销毁、慢旧章切快新章、同页十次换含图书：统计真实 URL create/revoke 总数及仍被引用的集合，静止后数量有界且图片仍显示。路径覆盖空格与字面 `%20` 同包、中文、`%23/%25`、编码点段、越包根；同一 SVG 的两个 view 显示各自区域。编码路径缺陷未证明能访问包外磁盘，不得扩大表述。

### F14 · P2 · 普通 EPUB 兼容与首次打开需要补齐

**证据与位置：**

- R08：显示名 `100%完成.epub` 导致 `URI malformed`，初始化失败。[openFromUrl:845](<D:/Temp/Lyra-Extension-Epub/reader.js#L845>)对 URLSearchParams 已经解码的 name 再解码一次。
- EPUB3 `<li><span>第一部</span><ol>…</ol></li>` 分组会被[目录解析:297](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L297>)提升成子章，丢掉“第一部”和一层深度。
- [加密描述检查:161](<D:/Temp/Lyra-Extension-Epub/epub-book.js#L161>)只要看到 EncryptedData/EncryptionMethod 就整书拒开，连正文未加密、仅采用标准 IDPF 字体混淆的书也误报 DRM。
- [首次位置:767](<D:/Temp/Lyra-Extension-Epub/reader.js#L767>)无保存位置时从索引 0 开始，没有跳过首项 linear=no；中途前后章已有跳过逻辑，首次入口也应一致。

**修正与通过：** 名称仅在所属编码层解码，覆盖 `%`、`%20` 字面文本、中文、空格与 `#`；保留不可点击目录分组及层级；识别字体混淆与受保护正文的区别，统一样式下可使用系统字体继续阅读，不需要为本轮实现 DRM 解密；首次默认进入可线性阅读的第一项，同时目录仍能访问辅助内容。为四项各增加最小合成 EPUB。

## 4. 原 A01–A25 的本轮判定

“部分正常”表示只有列明的子场景得到支持，不代表该整组通过。判定以原任务书完整标准为准。

| 原 ID | 本轮判定 | R2 要点 |
|---|---|---|
| A01 | 基本正文/目录分离与片段正常；完整边界待补 | 保留已通过用例，联测首次 linear=no 与最后意图 |
| A02 | 不通过完整要求 | F14 无链接分组；保留 NCX/无目录对照 |
| A03 | 不通过 | F13 编码路径、SVG 片段；补跨章脚注返回 |
| A04 | 已测重复旧投喂正常；整组待补 | 覆盖三入口及新宿主无多余编码/整书分配 |
| A05 | 不通过 | F03 慢章/慢书实测失败 |
| A06 | 不通过完整要求 | F03/F11 同窗失败重试、遮罩恢复 |
| A07 | 不通过完整要求 | 默认设置正常，F02/F04 非默认及高亮失败 |
| A08 | 未满足完整通过条件 | 原回报有部分宿主重开证据；需修复后真实重开/退出重启并证明候选身份 |
| A09 | 改名共享子例正常；待补同名同大小异内容 | 两类必须分别验证 |
| A10 | 不通过 | F02/F04；设置按钮必须实际可用 |
| A11 | 覆盖不足 | 晚图/字体就绪/末页意图与取消联测 |
| A12 | 原断言不足，未确认通过 | 按实际页面计算 20 次净位移，跨章、反向及取消 |
| A13 | 静止无 rAF 子例正常；其余待补 | 60/144/240Hz 或可控时间源 |
| A14 | 已测选区子例正常；整组待补 | 真实设置入口、复制/右键/链接/输入框 |
| A15 | 520/760/1400 宽度子例正常；完整矩阵待补 | 100%/150%/200% 缩放、可操作面板和行尾 |
| A16 | 不通过完整要求 | 事件属性隔离正常，F12 协议边界失败 |
| A17 | 不通过 | F05 定位、取消、跨书、范围与数量 |
| A18 | 不通过 | F04/F09/F10；本地与宿主均覆盖 |
| A19 | 不通过 | F06/F08；补真实双窗固定时序 |
| A20 | 不通过 | F10 读失败清空与本地假成功；保留只读失败对照 |
| A21 | 部分证据，完整矩阵未通过 | F11；新旧四组合、PDF 0.3.0、有书/无书 |
| A22 | 尚不足以整组通过 | 两本真实长书与插图书做首/中/末章及设置、查找、书签、重开 |
| A23 | 原压力证据不足且资源诊断失败 | F13；同一页面真实切章/换书，含图片与并发 |
| A24 | 不通过完整要求 | F03/F05/F07/F11/F13；关闭取消与资源收尾 |
| A25 | 本次包能独立加载，运行文件齐全 | 清理冗余库；R2 新包重新空目录验收 |

两本现有小说的结构检查显示分别为 373 与 255 个 spine 项，均没有本轮发现的编码路径、SVG、字体混淆或 linear=no 边界；它们能够打开不能替代合成边界用例。A22 要求首/中/末章抽检与指定操作，不要求人工读完所有章节，不上传原书。

## 5. DeepSeek 执行顺序与交付约束

建议按以下顺序做，每步完成对应回归后再进入下一步，保留现有有效模块拆分和正常路径修复：

1. **阅读与任务身份：** F01、F02、F03、F04；先保证设置可操作、正文不缺页、最后意图有效、位置坐标稳定。
2. **保存数据完整性：** F06、F07、F08、F09、F10、F11；先跑隔离原消息入口与故障注入，再跑原生宿主。
3. **搜索、资源与 EPUB 边界：** F05、F12、F13、F14；搜索和锚点共享统一文本模型，不再各自按 quote 猜位置。
4. **完整回归和产物：** 修正 A01–A25 对应关系；补强断言；执行真实宿主兼容、长书/插图和性能资源测试；生成 R2 独立候选。

测试按用户操作入口覆盖，必要时使用受控延迟和失败注入。断言使用已知目标文字、独立 DOM 可见范围、真实磁盘内容、协议回执与资源 create/revoke 记录；不要同时用同一套有缺陷的 capture/locate 函数互相证明正确。超时或 probeError 必须使正式用例失败或明确标为未完成，不能吞掉异常后记 PASS。

R2 产物要求：

- 给出新的扩展提交/差异、宿主差异、文件清单、构建与打包命令、候选 SHA-256；空目录解压验证全部依赖。
- 清除冗余 `lib/lib/jszip.min.js`，检查相对路径和版本标识。保留正确库、许可证、运行模块。测试、合成书、截图及真实小说不入发布包。
- 当前[调试导出](<D:/Temp/Lyra-Extension-Epub/reader.js#L1064>)暴露可变 state 和分页器，不是回报所称“只读调试”。改为明确的测试开关或受限只读诊断接口；正式包不依赖测试直接改内部对象才能运行。
- 保留 R1 报告、候选包和本轮证据。新记录使用 R2 目录；诊断工程复跑也使用新的输出根，避免覆盖原结果。
- 按原任务书保护现用安装、书库与现有状态；使用隔离候选和数据副本。不要直接运行既有部署脚本、结束用户程序或自行发布。

真实宿主验收必须记录：**PID、可执行文件路径、程序集 SHA-256、实际扩展目录、实际状态文件路径**。固定单实例锁可能让新候选启动后激活现用旧程序；只有候选目录和截图不足以证明运行的是候选。采用原任务书允许的独立测试实例，或在用户正常退出现用程序后做最终集成检查。遇到外部条件未满足，准确报告该项未完成。

## 6. R2 回交格式

在原文档目录新增 `DeepSeek实施回报-R2.md`，保留 R1。内容至少包含：

1. **F01–F14 逐项表：** 修复位置、根因、设计变化、回归步骤、实际结果、日志/截图路径。没有修完的直接写未完成及原因。
2. **A01–A25 完整表：** 使用原编号；每个子例列 PASS/FAIL/未测，区分普通浏览器、模块诊断、宿主事件替身和真实 WebView2。整组有必测未完成就不标整组 PASS。
3. **保存和取消时序：** 用合成数据展示双窗、换书、关窗、读取失败的请求身份、资格/序号、最终文件；解释新规则如何拒绝迟到旧请求。
4. **锚点与搜索实例：** 第二个重复命中、跨节点关键词、高亮后重开、慢图末页的“目标文字→实际可视范围”证据。
5. **真实兼容与性能：** 候选身份五项、四种新旧 EPUB/宿主组合及 PDF、有书/无书；真实书结构与抽检；含图压力下的 URL、DOM、内存趋势，静止后情况。
6. **交付清单：** 新候选包、宿主可运行目录、构建/打包命令、所有产物散列、变更清单、待 GPT 复核事项。

回交后由 GPT 复核实现与实际行为，再决定是否需要 R3。R2 不以“原测试全部绿”作为完成标准，而以已知缺陷修复和原任务书完整验收证据为准。

## 附：本轮浏览器与模块诊断入口

- [身份核对脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/prepare_review.py>)。
- [主浏览器探针](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/review-browser.cjs>)、[边界探针](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/review-boundaries.cjs>)、[高亮锚点探针](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/review-anchor.cjs>)、[模块探针](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/review-models.cjs>)。
- 宿主诊断工程、源文件链接方式和新输出目录复跑命令见[宿主专项报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r1/host-audit/宿主保存链路独立验收-R1.md>)末尾。

本轮使用已有 Playwright 与 Edge，无须安装依赖：

```powershell
$env:PLAYWRIGHT_MODULE='C:\Users\Administrator\deepseek-harness\node_modules\.pnpm\playwright@1.61.1\node_modules\playwright'
$env:LYRA_BROWSER_PATH='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
```

这些是针对 R1 冻结副本的诊断入口，包含故障观测而非全为成功断言。复跑会写其结果目录；应先复制到新的 R2 诊断目录、显式指向 R2 候选并调整为正式回归断言，保留本轮原始证据。不得把仍测试 R1 副本的结果交作 R2 验收。
