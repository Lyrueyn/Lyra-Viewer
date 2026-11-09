# EPUB 优化：GPT 独立验收结论 R3 暨 DeepSeek 返工任务书 R4

**结论：R3 不通过完整验收，进入 R4，不能发布。** R3 确实修复了图片锚点、普通搜索取消、超缓存漏搜、迟到书签和正文回滚等问题；但默认宿主设置保存存在整数精度故障，保存失败仍可能发送成功关闭回执，加载早期关闭会失去退出期限。此外，旧任务仍能撤销新书当前图片，位置回执和本地故障状态也存在归属问题。

这次结论包含三种来源：GPT 在冻结候选上的真实 Edge 交互；原 JS→原 C# 消息往返和隔离真实文件；DeepSeek 按指定场景追加的真实 WinUI/WebView2 证据。后者由 GPT 核对日志、身份和截图，不能改称 GPT 亲自执行了所有原生窗口操作。失败断言数不等于独立缺陷数。

继续按既定分工：GPT 设计和验收，DeepSeek 实现。保留全部 R1/R2/R3 冻结物，在新的分支/提交、候选和证据目录实施 R4，不发布、不覆盖用户安装、不修改用户数据。[原任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md>)、[R3 任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R2暨DeepSeek返工任务书-R3.md>)继续有效；未测项目不能因本轮聚焦阻断问题而取消。

## 1. 对象与证据索引

| 对象 | 本轮身份 |
|---|---|
| 扩展 | 提交 `ba6f763f9579a808ddc201b82b5867412265c104`，版本 0.7.0 candidate；工作区初始干净 |
| 候选包 | [0.7.0 候选](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidates-r3/lyra-ext-epub-0.7.0-candidate.zip>)；SHA-256 `847D8047702E8DE6A8DEC8B7AE9E3FB60693686400B71AA65C2B134A7270636C` |
| 包/源一致性 | 11 个包内文件均与提交工作区逐字节一致；完整保存在[身份记录](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/identity.json>) |
| 宿主 | 构建输出及候选运行目录的 DLL 均为 `8067990EBBAF126239B1F3B8CC936D36CB3B1F47DC317D541FDF4CE9C4A9905E`；四个宿主源文件另存冻结快照 |
| 原实施报告 | [R3 报告快照](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-report/DeepSeek实施回报-R3.md>)；SHA-256 `DA4E957A5C7963714066D766712D6F91D0C9B9CDA43E5F68902467FC1284E6B2` |
| 原生补充 | 首批 44 文件另存[补充证据快照](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement>)，逐文件散列见[清单](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/native-supplement-identity.json>)；纠正兼容样本的25文件另存[追加证据](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-compat-addendum>)和[追加清单](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/compat-addendum-identity.json>) |

核心入口：

- [宿主、状态与通信独立报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/host-audit/宿主状态通信独立验收-R3.md>)：正式结果为 `r3-release-report` 这一轮；39 个断言中 24 通过、15 未满足预期，无 probeError。包括原始消息、源码行、真实文件拒读/拒写及兼容桥。
- [搜索、解析与资源独立报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/model-audit/解析搜索资源独立验收-R3.md>)：34 条观察，25 PASS、6 FAIL、3 OBSERVATION；包括真实 ZIP、DOM、原 reader 事件链与资源账本。
- [R1 主路径复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/browser-evidence/review-results.json>)、[边界复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/boundary-evidence/boundary-results.json>)、[R2 新增问题复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/advanced-evidence/results.json>)、[本地故障复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/storage-evidence/results.json>)。
- [新增 Edge 时序结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/race-evidence/results.json>)：C01/C04/C05/C06；[失败快照恢复细化](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/race-evidence-v2/results.json>)：C02；[普通下一页点击的旧位置回执](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/image-ack-evidence/results.json>)：C03。
- [真实图片锚点刷新复测](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/image-reopen-evidence/result.json>)：B15。
- [实施方原生补充说明](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/README-原生补充证据.md>)与[结果汇总](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/native-supplement-results.json>)：实际双窗、图片重开、早期关闭、兼容性；其中误标的旧 EPUB 样本不能采用，详见第 6 节。

执行边界：使用现有 Edge、Playwright、Node、jsdom、JSZip 和 .NET，不安装新依赖。审计工程链接原始冻结 C#，事件及网页几何采用公开替身；修改版本计数器起值的隔离用例会明确注明。GPT 未改产品、未运行候选桌面程序、未部署或发布。

## 2. 确认进展及 R3 十二组当前状态

本表编号严格沿用 GPT 的 R3 任务书，避免实施回报中从 R3-06 起的重排混淆。

| R3 要求 | 独立确认的进展 | 仍未关闭 |
|---|---|---|
| 01 偏好快照与顺序 | 普通在途28→32不会再被旧回执清空；在安全整数夹具下，原R2字段防抖反序例正确 | 默认版本精度错误、串行授予边界、幂等/空窗设置、关闭时漏掉新pending |
| 02 会话/激活/序号 | Reload/NewDocument淘汰旧hello、旧source；重新获焦不复活旧epoch；中文/emoji语义幂等；时钟回退序号递增 | 偏好请求作废位置资格；快照仍未完全绑定操作/打开代次 |
| 03 finalize | 客户端handler已存在；无修改真实关闭有准确requestId/session回执；已初始化后的导航失败不取消期限 | 保存失败仍ok:true、只等旧flush、初始化后续导航清理掉关闭期限；缺session仍可被接受 |
| 04 故障降级 | 坏位置/书签不再阻断正文；失败快照保留；拒读不清偏好；宿主坏文件备份失败保留原字节 | 坏A污染健康B；本地旧快照覆盖新状态；重开未采用保留位置 |
| 05 搜索身份/取消 | 冷取消后立即新查询、热取消、输入防抖关闭、Enter去重、跨书旧列表/按钮均改善 | 点击结果后的导航等待中改词，旧结果仍提交高亮 |
| 06 搜索范围/模型 | 恰1000与1001正确；首/中超缓存章之后仍扫描后章；普通hidden/details/defs/desc排除；重复/inline/emoji坐标正确 | hidden br与SVG隐藏语义；稀疏热扫描无让出保证 |
| 07 书签 | 浏览器迟到添加回执不进新书；摘要256/257、UTF-8单条4096/4097及0/1/50/51/499/500/501主要边界一致 | 空白ID两后端不一致；其它完整A18组合仍须补足 |
| 08 图片/元素锚点 | B15真实保存第24/24页，刷新仍24；原生补充末页图片27/27关窗重开/完整重启仍可见 | 位置回执按null文字偏移错误清理新图片页；晚资源/重排矩阵仍不完整 |
| 09 资源任务 | 同书A/B迟到、同路共享图片交错、200次模块含图过渡资源/任务各1通过 | 跨书owner数字碰撞撤当前图；早期prepare失败留下空任务集合 |
| 10 外层协议 | 原回调拦截javascript/data/file，允许http(s)，内外两层各自负例通过 | 保留回归，无本轮确定失败 |
| 11 字体混淆 | 精确IDPF算法、普通XHTML/未知算法/Adobe拒绝已修 | 不存在ZIP字体和伪字体MIME仍放行 |
| 12 实际文档回滚 | B10在原setSection已提交新iframe后注入异常，恢复旧正文；模块确认旧图引用也恢复 | 后续组合覆盖保持，不能把prepare第一句抛错当全部中途失败证明 |

R1/R2 的设置按钮、36px切章37页、文字第8页原字符恢复、旧慢章/慢文件、重复第二处搜索、51条书签分页、百分号文件名等回归未见退回。原诊断 R03 仍引用已废弃的textNodePath而probeError，B01原DOM几何已经替代；它不是产品回归。

实施方13项浏览器断言在本轮快照重跑全部通过，6项模型断言也全部通过。这说明现有断言覆盖有限，不能据“全绿”覆盖本次新增失败。诊断退出码0仅代表执行结束。

## 3. R4 阻断修复清单

### R4-01 · P1 · 字段版本必须无损跨越 C# 与网页

关联 R3-01、A08/A19。证据 HA3-01/J01，以及原生 NATIVE-PREF-TWO-WINDOW。

[宿主计数器](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/host-snapshot/ReaderStateStore.cs#L31>)以 DateTime.UtcNow.Ticks 起步，约 `6.39×10^17`；[网页取版本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader-state.js#L271>)转成Number，超过精确整数范围。正式原JS→原C#测试授予 `639287159852507025/026`，两次回传都变成 `639287159852507000`，正常18→32设置均PREFERENCE_STALE，偏好文件不存在。

实施方按指定场景原生补测也确认：A字号18、B字号32、A行距2.2的初始提交均被拒；某次后续提交的值偶然可以往返，才写入字号32，行距仍丢失。因此不能称“永远不能保存”，准确结论是默认授权值无法可靠传输、设置保存不可靠。

**设计要求：** 采用宿主不透明字符串授权，或有明确容量/持久化设计的安全整数；协议解析、存储、比较和重试全链一致。优先不要把时间戳当授权身份。明确已有R2/R3磁盘字段版本的迁移，不可清空旧设置以逃避版本范围问题。

**必测：** 原UIinput→原宿主授予JSON→原网页解析→原提交→实际偏好文件；连续授予、多个字段、宿主重启、现有大字段序号、同操作重试。不允许只在C# long里测试，也不允许把审计的安全计数器夹具作为正式修复。

### R4-02 · P1/P2 · 偏好授权与阅读位置资格分离，并绑定真实操作

关联 R3-01/02，A19。证据 HA3-02/07/08，J04/J07/J13/J18、H13/H15。

1. **常规同窗冲突：** 原pageNext获位置token1；随后字号input申请偏好版本，宿主同一个GrantInteraction把位置token改成2，客户端原位置仍带1，自动保存被STALE_INTERACTION，位置文件仍null。不是需要双窗口才出现的错误。[宿主入口](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/host-snapshot/ExtensionHostWindow.xaml.cs#L389>)没有按purpose分开授权。
2. **顺序边界：** [客户端队列](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader-state.js#L269>)等待前一响应才发送后一操作。受控A行距回执延迟→A较早字号18排队→B较晚字号32先授予落盘→放开A，旧18反获更大版本并覆盖。该例两个事件替身保持可接受活动epoch，**未证明自然OS焦点切换时必然发生**；失活后旧epoch被拒的修复有效。
3. grant只绑定bookId/version/session，没有字段或操作ID；同一授予值可以改另一字段。成功写入但丢回执后，同值同版本重试又被PREFERENCE_STALE拒绝，pending不能确认完成。
4. 无书窗口仍可调整设置，却以空bookId申请版本，BAD_BOOK_ID后Promise固定为0；提示“稍后重试”但没有完成路径。

**设计要求：** 偏好操作不改变阅读位置owner/token。接受用户输入时立即确定客户端操作身份、字段集合、原书或全局偏好作用域、导航/激活代次；发送顺序不得依赖前一请求回执才赋予旧操作新优先级。宿主核对字段授权，明确同操作同值幂等及不同值冲突。空窗偏好定义正常全局保存或明确禁用，不保持不可完成的pending。grant回收必须有序，不能随机淘汰仍在途的授权。

**必测：** 翻页→改字号/主题→等待自动保存→重开；两个不同书的设置字段；主题同时变三个字段；延迟版本授予/回执、丢回执重试；无书设置；4096附近授权回收。安全整数夹具只能隔离其它缺陷，最终必须用默认候选全链执行。

### R4-03 · P1 · 关闭完成条件要覆盖最新修改、失败和初始化早期

关联 R3-03、A08/A24。证据 HA3-03/09、J05/J06/H06，原生待偏好关闭和加载中关闭。

**已确认三个独立分支：**

- [finishAndAck](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader.js#L1301>)吞掉flushPrefs/flushSave返回的false，而两者本就内部catch后return false；保存失败仍ok:true。原生[日志](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/close-with-pending-prefs-raw.log#L48>)明确是PREFERENCE_STALE后host.finalized ok:true、CLOSE_ACK revision0，磁盘无行距。
- 18已提交、回执暂留，输入32后关闭；只等待旧prefsFlushPromise，旧回执到达便ack。新32仍pending，80ms重试没运行，实际磁盘18，宿主已关闭。关闭应等待最终目标，不应依赖退出后的定时器补写。
- **初始化早期关闭悬挂：** 原生00:28:07.289发生CLOSING，之后才NAV/app.ready/hello/load/ready，十秒后仍可见且无timeout。[原始日志](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/close-during-loading-after10s.log#L235>)与[观测事实](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/close-during-loading-stuck-facts.txt>)相符。[NavigateToExtension:122](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/host-snapshot/ExtensionHostWindow.xaml.cs#L122>)会调用CleanupTemp，而[CleanupTemp:577](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/host-snapshot/ExtensionHostWindow.xaml.cs#L577>)又显式停止_closeTimer；StopTimers跳过它还不够。

此外宿主仍允许正确requestId/currentSource但缺session的回执；错误非空session已拒。此项是关联契约缺口，不据此宣称跨源伪造。

**设计要求：** 关闭是有界的收尾流程：冻结最终位置/设置目标，禁止新输入及初始化/导航续跑，停止旧任务和所有相关防抖；等待已有请求及必要的后续提交。返回明确的“无修改 / 已确认 / 失败 / 过期 / 超时”，成功回执仅用于全部目标得到确认。偏好或位置失败需准确说明，不能被其它字段成功覆盖保存状态。临时书目录清理与窗口最终销毁分开；关闭期限不由加载、重试、目录清理取消。WebView尚未创建时也必须可退出。严格验证requestId/session/source；保留ack、error、timeout不同日志。

**必测：** 初始化前、导航前、页面已载入但未就绪、读章中、冷搜索/扫描中、动画中、待版本、待位置回执、18在途后新32、写失败、无回执、错/缺关联。每个原生关闭记录起止时间、是否仍有窗、最终文件与完成原因。原提交“搜索中关闭”是在输入60ms后，早于300ms防抖，最多证明待搜索输入时关闭，不能替代已开始扫描的用例。

### R4-04 · P1 · 位置回执按操作版本清理；失败重试不得覆盖新位置

关联 R3-02/04/08、A07/A08/A10/A20。证据浏览器C02/C03、HA3-04/J09。

**C03真实Edge：** 纯图书点击下一页到第2页，实际写入后延迟回执；再点击下一页到第3页；释放旧回执，静置650ms。最终界面第3页，本地存储只有第2页，modified=false，只发过一笔保存。[清理判断](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader.js#L377>)只比较textOffset，两个图片页都是null，新操作被当成旧操作清掉。本例两次都是普通“下一页”点击，不使用内部定位函数。

C03早期两个页码输入探针最终存对了，是额外420ms输入防抖后来补救；它们保留为对照，不标失败。普通下一页点击没有该定时器，才暴露真正回执错误。[最终结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/image-ack-evidence/results.json>)记录前后元素路径与保存次数。

**C02真实Edge：** 第5页写失败→换书→恢复存储→重开原书，失败快照成功写回本地存储第5页，当前正文却显示第1页、原文是paragraph0。已等过旧输入防抖，排除了该干扰。[细化结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/race-evidence-v2/results.json>)证明R3“保住快照”有效，但“重新打开恢复位置”未完成。

**J09原调用链：** 保留失败offset100，原书本地键已有较新offset900/revision8；通过原file change重开，load读取900后自动retry又写回旧100/revision9。较新磁盘前提受控提供，不宣称原生双窗自然频率。

**设计要求：** 快照冻结原书实例/bookId/openIntent、位置、操作版本、原资格请求/返回token、保存序号和基准修订。await后不能重新取全局token/currentBook补装。回执只清除它确认的操作版本，不能仅比较文字偏移或页号；同章纯图、不同章相同offset都要安全。失败记录同样按版本管理，旧成功/失败不能删除或重建较新pending。重开应先裁决磁盘状态与未确认快照的优先级，再决定可见恢复位置；本地重试也要做冲突检查，不把旧内容提升为新操作。

**必测：** 上述C02/C03/J09；同offset跨章、连续两笔保存反序返回、旧失败回执晚于新成功、换书时等待interact、同书重新打开、重试资格过期。保存成功提示要对应当前操作。

### R4-05 · P1/P2 · 损坏状态按书隔离，书签校验保持后端一致

关联 R3-04/07、A18/A20。证据浏览器C01、HA3-05/10、J14。

坏A的position/bookmarks各置损坏标志后，打开全新健康B，B返回无warnings，但保存位置/添加书签仍LOCAL_CORRUPT，本地存储B两个键都不存在。原坏A字节确实保留。原因是[全局_localCorrupt](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader-state.js#L57>)不按bookId保存，也不根据后续成功读取更新。

**设计要求：** 每书位置/书签独立故障状态，偏好按其全局作用域管理；每次读取给出当前结果，不把曾经的故障扩散到别书。保留坏原件、备份失败与拒读保护。书签空白ID本地仍接受而宿主拒绝，应使用同样的非空白规则。主要256字符/4KiB等边界已通过，不需推倒重做。

**必测：** 坏A→健康B完整阅读/保存/书签→再回A；位置/书签分别损坏；拒读恢复；同书重新打开；ID空字符串/空白/超长与合法中文；失败/成功书签回执跨书仍隔离。

### R4-06 · P1/P2 · 资源释放绑定原书实例，早期失败也释放任务元数据

关联 R3-09、A05/A23/A24。证据模型D01/D06、F06/R06与浏览器C04。

旧书章1的owner2读取挂起→打开新书→新书也切到owner2章→释放旧读取。[过期分支](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader.js#L891>)使用全局state.book.releaseTask(2)，实际撤销新书当前图片。Edge中旧读取前fetch当前blob成功，之后失败，资源集合为空，而正文仍引用同一URL且ready=true。图片已解码，naturalWidth仍1；**不把“地址已撤销”夸大成“当前像素立即消失”**。

另连续40次prepare在readText处失败，图片资源和pending为0，但_taskResources留下40个空Set，属于任务元数据无界增长，不是40张图片泄漏。

**设计要求：** task至少是原book实例+owner，所有成功/过期/异常/回滚释放都用原归属；prepared已带book引用，应贯穿整个导航。prepare自身在返回owner之前失败也必须清理自身记录。保留同书共享资源引用保护、并发去重和销毁保护。

**必测：** 旧书owner2→新书owner2；prepare等待、setSection等待、异常恢复交错；当前图片URL始终可用；40次失败静置；成功200次含图切章和同页10次换书，记录有效URL、create/revoke、任务集合、DOM和进程内存趋势。

### R4-07 · P1/P2 · 搜索结果导航在提交前复核查询，补齐可见模型和取消边界

关联 R3-05/06、A05/A17。证据浏览器C05/C06、模型D02/D03/D04。

- 查OLDQUERY并点击结果，在章节readText挂起时改为NEWQUERY，再放开。输入已取消旧查询，但[导航内:919](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/reader.js#L919>)仍先applySearchResult，随后外层才检查代次。Edge实际高亮OLDQUERY，active.queryGen4而当前5；[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/race-evidence/stale-search-click.png>)和结果均已保存。
- `<p>A<br hidden>B</p>`在Edge实际innerText为AB、br display:none，但查AB无结果。SVG visibility:hidden实际不可见，查INVISIBLE_TOKEN却有结果。普通hidden span/details/defs已修，不能将这些正例重复写成失败。
- 热扫描只按每128次命中让出。8Mi UTF-16无匹配/末尾单匹配、64章零匹配都在取消定时器执行前返回。样本仅0.5–6.9ms，证明调度保证缺失，**不证明这些样本严重卡顿**；冷DOM构建的大输入也需明确可取消边界。

**设计要求：** book/open/query/nav身份贯穿prepare、iframe提交、定位、高亮和保存；input、close、open意图使旧结果导航失效，不能等副作用发生后才guard。可见过滤先于br/块分隔；处理保留的SVG display/visibility/symbol语义，明确use引用、aria-hidden及可达隐藏内容策略。按时间片/工作量分块，不以命中数量作为唯一让出条件；如扫描未完成或读取失败，显示真实范围/错误。

**必测：** 旧结果点击后改词/关闭/换书/随后翻页，分别在prepare与布局后释放；hidden br/SVG与原文Range几何；零/稀疏/密集匹配；恰1000、同章/跨章1001、超缓存首/中章后续唯一词、重复/inline/emoji全部保留。

### R4-08 · P2 · 字体混淆目标必须确实存在并属于明确支持的类型

关联 R3-11。[算法精确比较](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/epub-book.js#L252>)已修，剩余是[fontTypes与目标检查](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/candidate/epub-book.js#L246>)仍用任意子串且只查manifest。

真实ZIP→Book.open测试：manifest称font/otf但ZIP没有该项，仍fontObfuscated=true；`application/x-not-a-font`、`text/x-woff-comment`也被接受。应核对一次路径解析后的准确ZIP条目，并使用明确允许的MIME集合/标准结构。保留精确IDPF正例、未知算法/XHTML/Adobe反例、混合目标和无效路径。合成测试只证明声明分类，不代表DRM解密或字体可解码。

### R4-09 · P2 · 通信契约必须能诚实协商，完成真正旧版矩阵

关联 R3-02、A21。R3改变必需字段却仍标protocol version1，仅以persistentState选择宿主保存。独立真实互配：R2客户端→R3宿主hello成功/host模式，随后旧interact与prefs失败；R3客户端→R2宿主也hello成功/host模式，随后BAD_SEQUENCE。

**设计要求：** 可以不承诺开发候选R2之间完整功能兼容，但必须明确协议版本或必需能力、字段类型和失败/降级规则；不支持新授权契约时不能误宣称可持久化。不要通过宿主程序集仍为1.1.12.0或扩展清单版本猜能力。原正式0.5.26/原1.1.12/PDF矩阵仍须按原任务执行，其证据身份与此次互配分开。

**必测：** 新新完整保存；不支持新契约的hello；缺失epoch/字段版本；新扩展+原宿主诚实降级；真旧扩展+新宿主有书/无书；真旧旧；PDF文件名、打开、查找、多窗口、有书/无书。每项记录实际运行PID、exe、DLL、真正加载的扩展入口散列和数据路径。

## 4. 需要落实到设计中的共同规则

本轮多项失败来自同一类问题：异步操作发起时有局部快照，完成时仍混用了当前全局状态。R4请先提交简短的通信/状态设计说明，再按其实现；这里是技术设计交付，不要求用户逐步确认。

- 位置、设置字段、书签、查询、导航、资源任务都有不可变操作身份和明确归属；成功、失败、过期、重试使用同一身份。
- 用户新输入立即更新意图；旧操作不能因排队、重试或重新获焦而获得新优先级。宿主授权与客户端墙上时钟分开。
- 保存回执只确认对应修改，关闭等待最终目标；后台定时器不能成为成功关闭后的补救方案。
- 坏数据保护与阅读可用分开；按数据作用域隔离故障，按版本裁决失败快照与新持久化状态。
- 协议校验和测试包括真实序列化往返，不让分别通过的C#与JS单测掩盖二者连接错误。

## 5. 原验收矩阵不能缩减

按[原A01–A25](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md#L371>)逐条回交，PASS/PARTIAL/FAIL/UNTESTED注明证据层级及候选身份。

| 范围 | R4 仍需完成/保留 |
|---|---|
| A01–A04 | 正文/目录顺序、NCX/nav、片段/脚注返回、编码路径、SVG实际显示与旧投喂去重回归 |
| A05–A06 | 现有慢书/慢章/回滚正例；新增跨书资源、点击中改词、早期/中途失败及重试 |
| A07–A08 | 文字和图片恢复；设置与书签同时持久化；真实关闭重开和主程序完全退出重开。图片补充正例不等于全部A08通过 |
| A09–A11 | 内容身份；文字/图像独立几何；字号、行距、宽度、侧栏、窗口；ready后才到的图片/字体及章末意图，证明延迟跨过待测边界 |
| A12–A15 | 可计算20次、混合反向/跨章；时间源/静置动画；选择/复制/输入；520/760/1400与100/150/200缩放截图 |
| A16–A18 | 双层链接/内容隔离；完整搜索范围/点击/取消；书签跨书回执、去重、重排、重启及容量 |
| A19–A20 | 原固定双窗时序、不同书设置字段；真实文件拒读/拒写/异常退出/坏文件；健康书不受别书坏状态影响 |
| A21–A22 | 真正新旧四组合及PDF；原两本373/255章实际小说的首中末、字号、重开及搜索，含图书；不上传原书 |
| A23–A24 | 同页200次切章、10次换书、含图引用与进程趋势；初始化/读取/搜索/动画/保存各阶段关闭，旧任务不得落到另一窗 |
| A25 | R4空目录解包，实际入口运行，所有本地依赖齐全；R3包完整性通过不能替代R4新产物 |

模块已完成的200次含图切章与10次换书是有价值的补证，Node/jsdom内存观察不替代原生/浏览器进程趋势。缓存限制为8,388,608个UTF-16单位的文本缓存，不是8MiB总进程内存。性能只报告真实规模、耗时、输入阻塞和环境，不编造提升百分比。

## 6. 报告与原生补充证据勘误

1. 原R3报告把A08写为idle rAF、A19写为单窗关闭、A23写为局部模块资源测试。DeepSeek已接受编号校正；仍按原任务矩阵要求执行。
2. 报告第68行“只关闭前刷位置、不在防抖提交”与实际代码及J07不符。360ms自动保存仍执行；本轮没有认定产品已退化成仅关闭保存。
3. 最终原生日志revision0证明无修改关闭有回执；较早revision3的保存已在关闭前约660ms成功。它们都不能证明在途修改已被关闭冲刷。新增pending关闭日志直接证明错误分支。
4. R3自带回滚测试在prepareSection开头抛错；GPT B10另在真实iframe已提交后抛错，后者恢复旧文档通过。不要混写覆盖阶段。
5. 首批“新宿主+旧0.5.26”的[身份记录](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-native-supplement/compat-identity.json>)给出的入口SHA为`5A38641DD9FE98B8A9FEB0E104995A3F91B548650AC2D588E140204857A0CC77`，实际109行且引用reader.js/reader.css，已是模块化入口。原基线commit `c12fe26275cdec67922cccb64d9b5f2b4c269cd2` 的入口为675行、SHA `88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A`。仅清单写0.5.26不能证明旧版身份；该样本不能记为真旧版通过。首批“旧宿主”实际来自61b53b2，也被实施方撤回为原基线证据；后续更正结果见下表。
6. 原生补充图片完整重启截图已核对，27/27页的蓝色LATE IMAGE确实可见；迟到位置用暂停/恢复A的WebView2渲染进程制造，B位置保留，属于明确注入的实际窗口证据。PDF截图显示1/50页及缩略图，证明基本打开，不证明搜索/多窗完整矩阵。
7. 原生“加载中关闭”截图本身只证明窗口仍可见；与关闭关联日志、十秒等待事实及CleanupTemp源码共同构成结论，不仅凭截图推断耗时。正常已加载关闭仍可快速退出。

8. 继承的B14诊断仍输出defect:true，但它只看当前modified和已写键，未检验R3新增failedSaves映射，不能据此断言快照被丢弃。C02/J09通过原重开链进一步确认：快照确实保留，实际问题是可见恢复和陈旧重试冲突。

最后收到的[真基线补测说明](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-compat-addendum/compat-baseline-c12fe26/README-真基线补测.md>)与[更正声明](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/submitted-compat-addendum/compat-identity-CORRECTION.md>)补上了以下有限原生证据。GPT已复算原旧入口为30973字节/675行/88FA…21A，并核对相关日志、运行身份及新旧UI截图；旧无效样本保留，不能与更正样本混记。

| 组合与身份 | 本轮可以承认的结果 | 尚不能推出 |
|---|---|---|
| R3宿主8067990… + 真c12fe26扩展，PID72996 | legacy字符串ready、正文/翻页；有书退出日志约1.509s为CLOSE_TIMEOUT；另有空窗UI/身份 | 无书关闭日志未在该段出现；不能从文件名含nobook推断该CLOSE_TIMEOUT属于空窗 |
| 新0.7.0 + 报告来源b978661旧宿主，PID55100，DLL57CC5765…AAF3CD | 构建/运行身份记录带b978661，截图正文3/9，页脚明确仅当前窗口保存 | 不是新新完整保存；不替代全部空窗/重复打开矩阵 |
| 真旧/真旧，PID69852，同旧DLL与88FA…入口 | 实施方补充基础打开截图及身份 | 不是完整交互/无书矩阵 |
| PDF0.3.0 + R3宿主 | 50页文档基本显示、legacy对象ready，另有空窗材料 | 首批身份未逐场景记PID；查找/多窗仍需R4重新执行 |

说明中的git archive/Expand-Archive换行原因未经本轮独立重演，本结论只采用已核对的原blob字节散列，不把该工具行为描述提升成技术定论。

## 7. 实施顺序与 R4 回交

先完成 R4-01/02/03 的保存协议、授权与关闭，再完成 R4-04/05 的快照和故障隔离；随后修 R4-06/07/08 的导航、资源、搜索和格式边界，完成R4-09及完整矩阵。保留R3正向回归，避免以全局清理或丢弃pending掩盖旧任务问题。

回交新增 `DeepSeek实施回报-R4.md`、`candidates-r4`、`r4-evidence`，宿主构建/运行目录使用新名称。报告包含精确提交、源码/包/DLL散列、通信契约变化、R4-01–09与原F/A编号映射、每项原始消息及实际落盘、截图、明确未测边界。日志不能截掉关键请求后声称有完整原始JSON；旧证据保留。

复跑入口见两个专项报告；浏览器入口是本轮[时序脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/review-races.cjs>)、[恢复细化脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/review-races-v2.cjs>)、[图片回执脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/review-image-ack.cjs>)及[图片刷新脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r3/review-image-reopen.cjs>)。对R4运行前复制/适配到新输出目录，不覆盖R3证据。适配允许改变内部诊断接口，断言仍对准用户行为与实际文件，不能删除失败断言换取通过。

**回交门槛：** 默认候选的实际网页↔宿主完整链通过，关键原生场景无虚假成功/无法关闭，原矩阵如实完成且修复无回退。完成后主动联系本验收窗口，GPT继续独立复验。此文件不构成部署或正式发布许可。
