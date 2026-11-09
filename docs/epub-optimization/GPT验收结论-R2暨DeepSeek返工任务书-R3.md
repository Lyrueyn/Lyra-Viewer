# EPUB 优化：GPT 独立验收结论 R2 暨 DeepSeek 返工任务书 R3

**结论：R2 不通过完整验收，进入 R3；不得发布正式版本。** 上一轮多项阅读主路径故障确已修复，值得保留；但仍存在已复现的偏好丢失/覆盖、图片页恢复回章首、跨书书签与搜索结果污染、搜索漏检和取消失败，以及宿主关闭/会话失效问题。此次判定基于冻结候选的独立运行结果，不只是检查报告或测试退出码。

分工继续有效：GPT 负责设计、任务书与独立验收；DeepSeek 负责实现、候选构建和回交。R3 在新的提交、候选目录和证据目录完成，保留 R1/R2 全部原物。原[实施任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md>)与[R2 返工任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R1暨DeepSeek返工任务书-R2.md>)继续约束范围；本文件细化剩余项，不取消未测标准。

## 1. 本次对象、方法与证据边界

| 对象 | 冻结身份 / 核验结果 |
|---|---|
| EPUB 候选 | [0.6.1 候选 ZIP](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidates-r2/lyra-ext-epub-0.6.1-candidate.zip>)；SHA-256 `65CF0784AD4A006BA2444D1A5B08EF2C354B116E71325DFCC5E9A2C194170063` |
| 扩展源码 | 提交 `c96c912ea1ab1e9a7a311620663ea4f6634a3d18`；解包 11 个文件逐个与提交源文件一致，原仓库工作区干净 |
| 宿主 DLL | 构建输出与实际候选运行目录各自计算散列，均为 `21932E2C246BF6152BA9C42834F60654A7A5AE239151B645F0EE8F14F17A30DA` |
| 独立快照 | [身份记录](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/identity.json>)记录包内各文件、四个宿主源文件及两个 DLL 的散列 |
| 实施方回报 | [DeepSeek 实施回报 R2](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/DeepSeek实施回报-R2.md>)；本次另留[报告快照](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/submitted-report/DeepSeek实施回报-R2.md>) |

本轮执行三层独立核验：

1. 使用现有 Playwright / Edge，在隔离解包候选上进行真实浏览器交互、DOM Range 几何、刷新与 localStorage 检查。合成书和数据均为验收专用。
2. 用原始冻结 JS、现有 JSZip / jsdom 和公开受控延迟验证搜索、解析、资源所有权及原阅读器调用链；涉及 iframe 几何替身的项目明确标注，不当作浏览器视觉结论。
3. C# Compile Link 编译冻结宿主/协议/存储代码，通过无界面 Activated、Navigation、Closing、WebMessage 事件替身驱动原入口，并读取隔离的真实 Windows 文件；原 JS 的 input / 防抖所发 JSON 再回放到原 C# 消息入口。不是原生 WinUI/WebView2 测试。

本轮没有改产品代码、部署、发布、安装依赖或结束用户程序。四张提交截图已查看，确实展示新扩展、旧扩展和 PDF 的阅读界面；它们与身份记录构成实施方集成证据，本轮未自行操作原生宿主重演全部流程，不能称为 GPT 已完成完整桌面验收。

核心证据入口：

| 证据 | 用途 |
|---|---|
| [浏览器旧问题复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/browser-evidence/review-results.json>)、[边界复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/boundary-evidence/boundary-results.json>)、[高亮恢复复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/anchor-evidence/anchor-result.json>) | R1 已知失败对照 |
| [新增浏览器结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/advanced-evidence/results.json>) | B01–B10：准确文字恢复、在途偏好、迟到书签、搜索、图片定位、刷新、快速翻页、故障回滚 |
| [存储失败浏览器结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/storage-evidence/results.json>) | B13 三种本地坏数据；B14 保存失败后换书 |
| [实际图片页保存与刷新](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/image-reopen-evidence/result.json>) | B15：真实生成并落盘的图片锚点，刷新由第 24 页回到第 1 页 |
| [宿主保存专项报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/host-audit/宿主保存链路独立验收-R2.md>) | HA2-01–09 的源码位置、步骤、输入、落盘和限制；22 个 C# 断言、11 个 JS 断言 |
| [解析搜索专项报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/model-audit/解析与搜索独立验收-R2.md>) | M01–M20 的完整证据、模块/调用链与限制 |

诊断脚本捕获个别错误后继续执行，**exit 0 不等于产品通过**。不把同一缺陷的多层断言相加成“独立缺陷数”。旧 R03 探针依赖已移除的 textNodePath 而发生 probeError，已由 B01 的独立 DOM 路径与原字符几何替代；不是产品故障。旧 R06 的判定范围有限，跨书结论使用 B06/M19 等有唯一旧词的用例。

## 2. 确认修复的部分，以及 F01–F14 当前状态

| 原问题组 | R2 独立结果 | 剩余范围 |
|---|---|---|
| F01 设置按钮 | **已修复**。单次点击可打开，关闭和再次打开有效 | 保留回归 |
| F02 设置后分页 | **基础路径修复**。36px 切章时实际/记录均 37 页；B01 第 8 页刷新后原字符仍可见 | 晚资源、行距/宽度/窗口组合和末页意图证据不足；图片锚点另见 R3-08 |
| F03 最后用户意图 | **主要旧故障修复**。慢文件不盖过新文件；旧章节 1400ms 迟到不再把新章第 6 页拉回首 | 同书旧任务资源、其它异步回执与中途失败仍需处理 |
| F04 高亮稳定锚点 | **文字主路径修复**。高亮、翻到第 8 页、保存、刷新仍在第 8 页及相同文字 | 混合章节的纯图页仍回章首 |
| F05 搜索 | 重复第二处和跨普通行内标签可准确定位；原 quote 猜测不再是正常路径 | 取消/重启/输入防抖/旧列表/上限/隐藏正文仍失败 |
| F06 多窗口资格 | 失活拒绝、token 精确值、owner、未来 token、倒序均改善 | 仅重新获焦后的旧 interact、旧 hello、时钟、中文幂等仍失败 |
| F07 换书与关闭保存 | 成功写入时，防抖期间立即换书能保留旧位置；动画定格已有实现 | 失败快照丢失，finalize 客户端缺失 |
| F08 偏好 | 单字段只发 patch，不再全量覆盖；不同字段共享锁对照通过 | 单窗在途修改丢失；防抖改变提交先后导致旧字段覆盖新字段 |
| F09 书签 | 本地 51 条均唯一读回；两后端 0/1/50/51/499/500/501 边界通过；宿主大书签按字节分页通过 | 增删回执归属、本地单项限制不一致 |
| F10 存储失败 | 关键 R1 拒读却可替换情形已保护原文件；只读写失败清 tmp；本地写失败不再虚报成功 | 本地偏好拒读后覆盖、坏位置/书签阻断阅读、换书丢重试快照 |
| F11 生命周期/协议 | 客户端完整响应信封、主要遮罩恢复、缺失/过期 loadId、外来 legacy 来源检查通过 | 旧协商复活、同源旧导航、关闭 deadline 被取消等未完成 |
| F12 危险链接 | 清洗层普通 HTML/SVG、伪造 data 属性已有有效防护；普通危险链接不再传给 window.open | 外层打开入口白名单未实现，不得宣称“双层已完成” |
| F13 资源/路径 | 同图真正并发去重、读中销毁、一次路径解码、完整资源字节区分、SVG 片段保留通过 | 同书旧章准备任务迟到资源仍残留；含图压力和 SVG 实际绘制未完成 |
| F14 格式边界 | 百分号文件名、无链接目录分组、首次跳过 linear=no 已验证；精确 IDPF 正例/AES 反例通过 | 字体混淆分类使用模糊正则且不检查目标类型 |

另外 B09 以实际每章 16 页计算 20 次前进的预期，最终第 2 章第 5 页正确；它补足了一个可计算的快速翻页正例，仍不替代原 A12 的混合反向输入和边界组合。

提交的 R2 浏览器脚本在隔离快照中重新执行，无 FAIL 输出；其中一个 status 为布尔 true。该事实与以上失败并不矛盾：原脚本未覆盖这些时序。下一轮应统一机器可读状态，并为真实失败给非零测试退出码。

## 3. R3 必须完成的问题单

### R3-01 · P1 · 偏好必须保留每次真实修改的身份和不可变提交快照

关联 F08/F10、A19/A20。证据 B02、HA2-01/02。

- **单窗可见错误：** 字号 28 已发送、回执被延迟时改成 32；回执返回后 pending 变空，只发送过 `{fontSize:28}`。界面为 32，实际 localStorage 为 28。Node 原 input → 原 C# 落盘也独立重现 18→32 丢失。
- **双窗字段顺序：** t0 A 改字号18，t100 B 改字号32，t200 A 仅改行距；B t400 提交32，A t500 提交旧18加新行距。当前客户端在提交时取 Date.now 序号，宿主按该数值接受，实际偏好文件最终字号18。没有篡改序号，也不需要系统时钟回退。

原因位置：[reader.js:217](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L217>)直接引用可变 pending 对象；[reader-state.js:222](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader-state.js#L222>)提交时生成序号；[ReaderStateStore.cs:307](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/host-snapshot/ReaderStateStore.cs#L307>)只相信客户端数值。

**设计要求：** 字段变化发生时即记录操作身份和宿主认可的字段版本；其它字段后改不能给旧字段重新获得优先级。提交冻结字段值、版本及请求身份。回执只能清除仍与该次提交版本相同的 pending 项，失败保留 pending；`applied=[]`/过期不能一概显示“已保存”。由宿主验证授予过的版本，不接受任意大数永久占据字段。

**必测：** 上述两种真实 input 时序；新字段在途加入；失败后重试；不同书分别改主题/字号；同字段反序回执；授予外的版本被拒。必须展示原始客户端请求及最终实际偏好文件，不能继续手填300/500/400替代操作链。

### R3-02 · P1/P2 · 交互、导航会话和保存序号必须共同失效

关联 F06/F11、A05/A08/A19。证据 HA2-04/05/07/09，均为原入口的定向时序/协议验证，不是原生消息乱序发生率测量。

1. A 较早 interact 延迟，B 保存后 A 仅重新获焦、未发生新阅读操作；旧 A 请求此时到达仍获更大 token，旧位置覆盖 B。
2. Reload 虽生成新 session，旧 hello 到达后，[宿主:277](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/host-snapshot/ExtensionHostWindow.xaml.cs#L277>)反向采纳旧 session，旧 token 保存重新成功。直接旧 save 对照确会被忽略。
3. NewDocument 未统一换会话，同源旧 legacy ready 仍可能撤新加载层。
4. [nextSequence:177](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader-state.js#L177>)遇合成时钟回退返回重复值，第二个不同位置被拒。相同中文位置重试还因序列化转义与原文字符串比较被误判 DUPLICATE_SEQUENCE。

**设计要求：** 宿主持有当前导航 session；hello 只确认匹配会话，不能选择宿主当前会话。交互请求携带宿主可核对的激活/导航代次，失活及导航淘汰旧请求；重新获焦本身不构成新阅读操作。同一有效资格内用严格递增操作序号，跨资格顺序由宿主 token/epoch 裁决，摆脱客户端墙上时钟。幂等按规范化语义或请求摘要比较。

位置保存还应冻结 bookId、book 实例/打开代次、position、操作序号、资格请求或已授予 token；不能 await 后改从全局状态拼装 token。[当前 flushSave:282](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L282>)只冻结了部分内容，此处为设计缺口，不单独宣称已经动态证明串书落盘。

**必测：** A延迟→B保存→A仅获焦→旧interact/保存；A获焦且真实新操作正常接管；旧hello/ready/error/save；NewDocument/Reload；时钟回退、新会话、同毫秒连续操作；相同中文/emoji/不同转义或属性顺序的幂等对照。保留已过的失活、owner、未来 token、倒序用例。

### R3-03 · P1 · 实现真正的关闭冲刷协议，关闭期限独立管理

关联 F07/F11、A08/A24。证据 HA2-03、B08。

冻结[客户端宿主消息处理](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L1127>)只有 openBook；发送 host.finalize 后产生 0 保存、0 回执。beforeunload/pagehide 只 flushSave，不 flushPrefs。浏览器里改字号32立即刷新后恢复22。宿主1.5秒正常等待期内普通300ms防抖可能成功，因此不声称每次普通关窗都丢数据。

[宿主关闭路径](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/host-snapshot/ExtensionHostWindow.xaml.cs#L416>)把回执和超时都记成 FINALIZED。Closing 后发生 NavigationCompleted(false) 会 StopTimers，连关闭期限一起取消，后续 Closing 又因 finalizePending 而取消，形成事件级无法正常关闭。空 requestId 的 finalized 也被接受。

**设计要求：** 增加完整 finalize handler：停止接受新输入、取消旧搜索/防抖/任务，冻结最后有效位置与设置；协调已在途提交，等待确认或明确失败后回传准确 requestId/session。宿主严格匹配本次关闭关联，关闭期限不受加载失败/重试清理影响；超时可退出，不能无限阻止关窗。日志分别标 `ack`、`timeout`、`error`，同时记录对应保存 revision。刷新也需明确冲刷策略，不把异步 pagehide 当可靠完成承诺。

**必测：** 无修改、位置防抖、偏好防抖、等待 interact、等待保存回执、加载中/导航失败、搜索中、动画中分别关闭；错/空关联回执；无回执必须有界退出；原生 WebView2 抓到真正客户端 ack 与实际落盘。旧扩展/PDF 无新协议时保留兼容关闭。

### R3-04 · P1 · 失败快照和三类本地状态都必须安全降级

关联 F07/F10、A06/A20。证据 B13/B14、HA2-06。

- B14：位置保存临时失败后换书，原书未保存，modified 被清除；恢复存储后没有保留的旧书待重试快照，界面却可显示新书“位置已保存在当前窗口”。原因是[换书:843](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L843>)忽略 flushSave(false)。
- B13：只损坏本地位置 JSON 或书签 JSON，原书无法打开、ready=false；原坏字节保留是正确的，但“阅读继续”未实现。损坏偏好对照仍可打开，不混为同一结果。
- HA2-06：本地已有字号32/sepia，受控 getItem 抛错但 setItem 仍可用；保存行距会从空对象写，删除原字号/主题。[本地偏好:281](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader-state.js#L281>)仍吞读错。

**设计要求：** 每书保留不可变待确认/失败快照及状态，换书不得丢弃；重试只能使用原资格，过期明确标为不能覆盖，不申请新资格抢写。位置/偏好/书签统一区分不存在、损坏、拒读、拒写、未知版本；保存前读取失败不得当空对象。可读模式与持久化成功分离：坏状态不阻断原书正文，未确认的数据不给成功提示。备份失败时也不能假定原数据已经保护。

**必测：** 三类存储缺失/损坏/拒读可写/拒写矩阵；失败后换书再恢复；宿主降级本地后继续翻页、书签与偏好；备份失败分支；原文件逐字节保持。保留 R2 已通过的真实文件拒读可替换和只读写失败对照。

### R3-05 · P1 · 搜索的输入、列表和按钮都必须绑定产生时的任务身份

关联 F03/F05、A05/A17。证据 B04/B05/B06、M05/M06/M16/M19/M20。

- 输入后立即关闭，防抖到时仍读3章并填3个结果；关闭没有清 searchInputTimer。
- 冷索引查询取消后立即重新查询，新查询复用旧 `_building`，两者均“搜索已取消”。热缓存查询也有在 await 后才捕获代次的漏洞。
- 旧书查 OLDONLY，换 FRESH 后重新打开搜索，旧列表仍可见。原 reader 调用链 M19 点击它，把旧 offset 套到新书，实际 Range 为 NEWNEWN，状态却仍写 OLDONLY。B06 已补浏览器旧列表可见证据；M19 的点击几何是替身，不夸大成浏览器已核对该点击的像素。
- 改词事件发生时不立即失效旧 query，旧结果能在新的300ms防抖前显示并被点击。

位置：[closeSearch:518](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L518>)、[input:586](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L586>)、[换书:878](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L878>)、[search:39](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/search.js#L39>)。

**设计要求：** 接受 input/close/open-intent 时立即取消旧查询、防抖与旧按钮；索引构建身份和查询身份分离，已取消构建不供新查询复用。结果及点击闭包携带原书实例、打开代次、查询代次，点击和每次 await 落地后复核。Enter 应清已有防抖，避免重复查询；用户随后翻页仍应获胜。

**必测：** 冷/热索引取消、取消立即重开、快速改词、input+Enter、close前防抖、换书时旧读取/已有列表/点击切章各阶段；所有最终高亮均核对当前书的正确命中原文与可见范围。

### R3-06 · P1/P2 · 搜索缓存上限不能截断全文，文字模型必须有可达语义

关联 F05、A17/A22。证据 M03/M04/M07/M12/M18。

- 第一章1000个命中、第二章再1个，返回1000且 truncated=false。
- 首章8,388,609 UTF-16单位，后章有唯一词：只读第0章，后章完全未扫描；UI 却显示“没有找到匹配内容”。`search.js` 与 R1 字节完全一致。
- 单章数百万匹配在一次同步 while 内完成，没有取消让出点；本机受控 Node 一次约110ms，只证明该输入的执行，不外推所有客户端时长。
- hidden、关闭details、SVG defs/desc均进入模型；`A<span hidden>HIDDEN_SECRET</span>B` 查可见 AB 返回0，查隐藏词反而返回1。

**设计要求：** 分离有界缓存与扫描范围，大章分块扫描、可回收缓存，达到缓存限额继续处理后章；跨章确认1000+，未完成/失败范围如实呈现；明确 UTF-16 code unit、实际字节与 UI 单位。定义统一的可见正文/隐藏可达内容策略：若搜索未展开脚注/details，应有展开或跳转显示方案，不能返回不可见零几何命中；SVG定义/描述不得无条件当正文。保留已修的重复词、跨inline、emoji坐标。

**必测：** 全书恰1000、同章1001、跨章1001；首/中章超缓存后章唯一词；长单章取消；英中/多空白/NBSP/emoji/块级分隔；隐藏内容与可见 AB；UI扫描范围与实际读取列表一致。

### R3-07 · P1/P2 · 书签增删回执隔离及单项容量统一

关联 F09、A18。证据 B03、HA2-08。

B03 延迟旧书添加回执，先打开 FRESH，再释放回执；新书书签列表出现 NORMAL 的摘要，状态提示“已添加书签”。[addBookmark:483](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L483>)和[删除回执:439](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L439>)仍直接修改全局列表。删除分支本轮为静态发现，新增已浏览器复现；不声称旧书签已经错误落盘到新书。

另外同一条6831 UTF-8字节、摘要257字符书签，本地接受、宿主 BOOKMARK_TOO_LARGE。本地只按 JSON 字符数8192检查。

**设计要求：** 操作从点击到回执绑定 bookId 和打开代次；持久化可针对原书完成，但回执不得改变新书 UI、资格或保存状态。两后端等价执行摘要≤256 UTF-16、单条≤4096 UTF-8字节、合法 ID、每书≤500、页≤50与完整响应字节约束。

**必测：** 添加/删除成功与失败回执跨书；同书重新打开也不能套旧 UI；重复位置；每个单项限制独立测边界；保留0/1/50/51/499/500/501和大中文书签完整分页。

### R3-08 · P1 · 图片锚点不能把 null 文字偏移转换为 0

关联 F02/F04、A07/A08/A10/A11。证据 B07、B15。

B15 在混合文本/图片章节通过真实“上一章”到第24/24页；captureAnchor 自动产生 `textOffset:null, elementPath:[104], chapterFraction:1`，该锚点实际已写入 localStorage。刷新后恢复到第1/24页，显示 FIRST 与段落0。B07 对有效图片元素定位也从实际第17页错误得到第1页。

原因：[paginator.js:501](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/paginator.js#L501>)执行 `Number(anchor.textOffset)`，null变0；空 quote 直接让第一处文字 Range 抢在图片元素回退之前返回。

**设计要求：** 锚点显式区分文字/元素/比例，严格验证偏移类型和上下文；null不是文字0。元素目标应定位实际图像或有效容器；文字回退失败再使用比例。重排保存内容锚点；“上一章末页”等尚待布局稳定的意图需明确处理，不被旧资源回调改写。

**必测：** 混合章节纯图页添加书签、保存、刷新、原生关窗重开、改字号/宽度；整章纯图；横竖插图；NULL/缺字段/非法元素路径；慢图片、字体后排版和章末返回。要验证具体图像可见，不能只比较两个内部页号。

本轮两次晚图尝试 [3.7s 结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/late-image-evidence/result.json>)、[8.5s 结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/late-image-evidence-8s/result.json>)在第一次 ready 时 incomplete 都为0，**未覆盖 ready 后晚图重排**，不能算 A11 通过，也不把它们当失败证据。R3 必须证明所测延迟确实跨过待测边界。

### R3-09 · P2 · 同书过期章节资源必须在任务结束后回收

关联 F03/F13、A23/A24。证据 M10。

两慢旧章 A/B 的图片读取挂起，快新章 C完成并 releaseResourcesExcept；放开 A/B 后，原 `_resourceBaseUrl` 仍创建两个 URL。全部 pending=0、页面静止时，资源由C的1项增至3项，旧2项未收。没有证明当前图被撤销，此例证明漏收。

位置：[epub-book.js:513](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/epub-book.js#L513>)仅判断整书销毁；[reader.js:760](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L760>)发现旧 prepareSection 后直接返回。

**设计要求：** 资源属于书/章节任务，prepared采用显式租用/引用或等价所有权；失效任务释放自己的资源，当前章共享引用仍有效。保留并发同图去重与destroy保护，不能用全局 clear 重新引入误撤新图。

**必测：** 慢A/B→快C→旧任务全部结束；共享图有效/失效任务交错；同页200次含图切章、同页10次换书后静置；create−revoke等于有效引用，记录进程内存趋势。文本零图片资源测试不能替代。

### R3-10 · P2 · 外层打开入口完成协议复核

关联 F12、A16。[reader.js:1052](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L1052>)仍直接 window.open；M17 定向通过原 onLink 回调送入 javascript URL，拦截器收到它。清洗层普通危险书内链接对照已经通过，**本轮没有证明清洗绕过、URL执行或沙箱逃逸**。

外层独立解析并只允许既定 http/https，拒绝其它协议、畸形输入和伪造控制属性。清洗层与外层分别做负例；保留 SVG href/xlink、事件属性、嵌套框架、宿主接口隔离与合法明确点击对照。不能只依赖入口前“已经清洗过”。

### R3-11 · P2 · 字体混淆按精确算法和目标资源识别

关联 F14。证据 M14。[epub-book.js:208](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/epub-book.js#L208>)只用 `/(idpf|font|obfuscat)/` 判断字符串；未知含font/idpf的算法指向XHTML也放行，精确IDPF指向XHTML也放行。

逐条读取 EncryptedData、精确受支持算法 URI、CipherReference 一次解析后的真实 ZIP/manifest目标。只有全部受保护目标确实为受支持的字体混淆才按系统字体继续；未知算法、正文/混合目标、无效目标给准确不支持说明。合成反例正文保留明文，仅验证分类，不代表解密成功或 DRM 绕过。Adobe等格式按本轮明确支持范围说明，不临时扩大为全部格式支持。

### R3-12 · P2 · 新书提交中途失败的回滚必须恢复实际文档

关联 F03/F10、A06。B10在原 setSection完成、已替换iframe后注入渲染失败：全局book恢复NORMAL，但实际h1仍FRESH-0，ready=true，状态却说已保留原书。[回滚:929](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/candidate/reader.js#L929>)只恢复状态字段，未恢复旧文档。

这是**受控中途异常**的事务恢复测试，不宣称该异常在正常书里必然自发发生。实现应在提交前保留可恢复的旧书上下文，失败时恢复实际章节/锚点/资源及书签设置，或展示一致且可重试的失败界面。未提交的新书资源也应销毁，旧正文未恢复前不能ready或保存坐标。测试覆盖解析前、状态读取、prepare、iframe提交后、最终布局/定位等失败阶段。

## 4. 原 A01–A25 的补交要求

原验收编号保持不变。R3 回报逐条写 PASS / FAIL / PARTIAL / UNTESTED，并区分“已有证据复用”“本轮重新执行”“原生宿主”“浏览器”“事件/模块替身”。不能用本文件几个新增回归代替整个矩阵。

| 原验收组 | R3 重点 |
|---|---|
| A01–A04 | 保留正文顺序、EPUB2/3、无目录、片段/脚注返回、路径/SVG与旧传书去重；SVG两个view要验证实际画面，完整资源字节不同不能只比PNG首字节和长度 |
| A05–A06 | 已修慢文件/慢章路径保留；补统一意图身份、旧结果/回执、中途失败实际文档回滚与重试 |
| A07–A08 | 文本可见字符正例保留，补图片页；真实关窗重开及完全退出主程序重开，位置/设置/书签一并验收；刷新和finalize关联都要留证据 |
| A09 | 同名同大小不同字节隔离；改名/复制相同字节共享，保留候选实测证据 |
| A10–A11 | 字号、行距、宽度、侧栏、窗口组合；长段/重复/emoji/高亮/图片；晚资源前后独立可见内容或末页意图，不只内部locateAnchor等于page |
| A12–A15 | 可计算20次及混合正反输入、动画可控时间源、拖选/输入焦点；520/760/1400与100/150/200缩放截图；不得用“测试做过”替代对应候选产物 |
| A16–A18 | 双层协议、内容隔离；本文件完整搜索时序/范围；书签迟到回执、统一单项限制、重启和几何 |
| A19–A20 | 真实输入到实际存储的双窗延迟矩阵、旧交互重新获焦；不可变偏好patch；三类存储拒读/拒写/损坏及阅读继续 |
| A21 | 四种新旧宿主/EPUB组合及PDF0.3.0，有书和无书；旧/旧仍未补齐。PDF按原任务验证搜索与独立多窗口，不把一张第一页截图当全部完成 |
| A22 | 两本实际373/255章小说的已有结构/首中末证据保留，补实际书搜索及含图书。原书不上传 |
| A23 | 原提交是200章纯文本的199次过渡与10次page.goto；不能代替200次切章、同页10次换书及非零图片资源压力。补DOM、资源计数与进程内存趋势 |
| A24 | 读取、搜索、动画、保存等待、导航失败中关闭；精确ack与超时分别验证；无后台任务和跨窗旧消息 |
| A25 | 新候选解压空目录逐文件对照源码/包清单，通过真实入口运行；当前R2包完整性已核对，R3仍需新身份 |

原生测试仍须记录 PID、完整exe路径、DLL散列、实际扩展路径和状态路径。固定单实例锁会导致“启动候选”实际激活已运行安装版；不得擅自结束用户进程或凭窗口标题判断身份。优先隔离测试程序/数据，最终主程序集成按原任务书执行。

## 5. 对 R2 报告的正式更正

DeepSeek 在审查窗口回复中已确认以下事实，冻结原报告保持原样，本节作为勘误：

1. 搜索模块与R1逐字相同。F05实际改动主要在共享模型、分页坐标与调用侧；缓存超限后继续扫描并未实现。
2. 冻结扩展没有 finalize handler。报告“冲刷后回执”不成立；实施方补充当时 CLOSING 20:06:49.985 → FINALIZED 20:06:51.501，约1.516秒，符合超时路径。原日志已被后续测试覆盖/清理，目前只有转述时间段，不将它当完整原始日志。独立代码/事件验证已足以确认该问题。
3. 外层 handleLink没有第二次白名单；报告“双层白名单”过度陈述。

报告承认未测的慢章、晚资源、搜索取消、压力等不因表格局部PASS变成完整通过。R3需逐条对应实际修改与测试；原日志、wire请求、保存文件和截图按测试运行标识归档，后续试验不能覆盖关键证据。

## 6. R3 执行顺序、交付与复验入口

建议依赖顺序：

1. 先确定导航/激活/操作身份与保存模型，落实 R3-01、02、03、04；先修数据覆盖、丢失和无法关闭。
2. 在相同身份规则下完成 R3-05/06/07，修搜索与书签所有迟到路径。
3. 完成 R3-08/09/12 的锚点、资源及提交回滚；完成 R3-10/11 的边界校验。
4. 跑针对性回归，再补原矩阵未完成项，最后构建冻结候选并交 GPT 独立复验。

R3 回交新增：`DeepSeek实施回报-R3.md`、`candidates-r3`、`r3-evidence`，宿主输出与运行目录另取新名。可本地分阶段提交；不得覆盖R2、不推送/发布、不改安装目录、不清用户数据。报告应包含所有 R3-01–12 及 F01–F14/A01–A25 映射、修改文件、提交号、完整产物散列、真实宿主身份、已测与未测边界。

本轮可复跑入口如下；输出会落到各自诊断目录，比较R3前先复制或指定新的运行目录，保护R2证据。

```powershell
$env:PLAYWRIGHT_MODULE='C:\Users\Administrator\deepseek-harness\node_modules\.pnpm\playwright@1.61.1\node_modules\playwright'
$env:LYRA_BROWSER_PATH='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
node .\gpt-review-r2\review-browser.cjs
node .\gpt-review-r2\review-boundaries.cjs
node .\gpt-review-r2\review-anchor.cjs
node .\gpt-review-r2\review-advanced.cjs
node .\gpt-review-r2\review-storage.cjs
node .\gpt-review-r2\review-image-reopen.cjs
node .\gpt-review-r2\model-audit\audit-models.cjs
node .\gpt-review-r2\model-audit\audit-reader-flow.cjs
```

宿主/客户端工程复跑方式见[专项报告末尾](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r2/host-audit/宿主保存链路独立验收-R2.md#L168>)。R3实现可以调整内部接口，测试应跟随对外契约和用户行为合理适配；不要为了保持旧诊断入口保留错误结构，也不要删除断言来获得绿色结果。

**交回条件：** 已修复部分无回退；以上确定缺陷有对应修复和实际结果；全部要求如实列明，原生关键链有可核对证据。DeepSeek完成后主动联系本验收窗口，GPT再次检查候选、差异、数据与行为，直到功能完整通过。R2不构成部署或正式发布许可。
