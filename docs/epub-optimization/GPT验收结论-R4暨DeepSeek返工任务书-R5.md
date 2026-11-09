# GPT 验收结论 R4 暨 DeepSeek 返工任务书 R5

**结论：R4 不通过，不具备发布条件。** 已证实的正向修复应保留；剩余失败涉及真实搜索漏项/错位、旧导航继续提交、关闭未保存位置却回成功、失败位置恢复，以及设置/资源异常分支。不能因原回归转绿就把完整功能判定通过。

本文由 GPT 负责设计与独立验收，DeepSeek 负责实施。GPT 本轮未修改产品代码、候选包或用户书库。R5 不发布、不安装到用户环境；新增隔离候选和证据，保留所有 R4 冻结物。

## 1. 被验收对象与证据边界

| 对象 | 核实身份 |
|---|---|
| R4 任务书 | [原任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R3暨DeepSeek返工任务书-R4.md>)，SHA-256 `20E37F00B70E7E7BD72CE072164049269CBE94998FBE147ABD50A7A59F989569` |
| 扩展 | 分支 r4，提交 `04f6ccb80546d6d04fe3c21f4a08775d83bfc0c6`，0.8.0；核对时工作区干净 |
| 候选包 | [0.8.0 ZIP](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidates-r4/lyra-ext-epub-0.8.0-candidate.zip>)，93,401 bytes，SHA-256 `B128B9DFBE9D7A1085CC3505F9B3EDC645847CFA24DA33958EB26EDD685F184B` |
| 宿主 | 构建目录和运行目录的 DLL 均为 `9EBA643FA6E3933B5A3F86AFC1BE424FFAA08A09BF11B9AC89D2C451E3A9DF95`；exe为 `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335` |
| 实施报告 | [冻结报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-report/DeepSeek实施回报-R4.md>)，SHA-256 `3168031DA48B7C4D127F3A5102641AA9E324CBEFD6FD8E8FB0E20DA5573AF207` |
| 代码与提交证据 | [身份记录](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/identity.json>)：11个包内文件逐字节等于源码及运行扩展；4宿主源码与提交清单一致；55份提交证据递归冻结，其中SHA256SUMS列出的54份全部通过散列核对 |

本轮有三种独立执行证据，不能相互冒名：

- **真实 Edge**：原候选完整页面、本地隔离HTTP源、真实DOM/Range/分页与localStorage。共49条观察，其中40条未复现缺陷、7条新失败、2条旧探针不作为产品判定。见[浏览器索引](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/browser-summary-final.json>)。本地存储不称原生JSON文件。
- **原JS→原C#**：原业务文件原样执行/Compile Link，经过真实JSON序列化，实际隔离文件；WinUI事件、DOM几何和传输/时钟可控替身。43条观察为30满足、13未满足，包含条件/压力边界，不等于13个独立阻断缺陷。见[宿主独立报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/宿主状态通信独立验收-R4.md>)。这不是运行候选WinUI DLL的原生自动化。
- **模块/jsdom**：真实ZIP、原解析/搜索/分页器业务，公开资源账本与iframe/几何桥；65条为43 PASS、15 FAIL、7 OBSERVATION，失败归五类。见[模型与导航独立报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/解析搜索资源导航独立验收-R4.md>)。R3原34条全部保留并复跑为31 PASS/3 OBS，原六条失败已修。

DeepSeek提供的原生日志、文件、截图由GPT核对，明确标作**提交的原生证据**，不写成GPT亲自操作了原生窗口。各层用例有重叠，计数不能简单相加作为“完整验收通过率”。

审计工具边界另有[SDK首用观察](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/sdk-first-use-observation.json>)：构建提示开发证书“已安装”，后续只读核对仅见早于本轮数月的既有localhost证书，未发现本轮新证书，也未执行创建/信任/删除证书命令。没有事前证书存储快照，不对SDK所有全局副作用作过度保证；复跑说明已显式禁止SDK生成开发证书。

## 2. 已确认修复及 R4 九组状态

| R4要求 | 本轮结论 | 必须保留的修复与仍需处理的分支 |
|---|---|---|
| R4-01 无损版本 | 核心修复通过 | 默认pg字符串真实往返，无安全计数器夹具；旧19位fieldSequences原始字节保留；顺序重启订单900→901→902正常。并发独立进程仅列条件边界 |
| R4-02 资格分离/操作语义 | 部分通过 | 翻页→改字号仍用原位置资格并实际保存；空窗和主题多字段通过。陈旧字段阻塞新字段、容量回收后的幂等和scope契约仍未闭合 |
| R4-03 最终关闭 | 部分通过，仍有P1 | 18实际写盘回执在途→新32→关闭会等32；真实只读偏好文件失败ack:false；无修改、严格关联、初始化早期期限通过。页码防抖/读章中仍会假成功，旧任务仍续跑 |
| R4-04 位置/失败快照 | 部分通过，仍有P1 | 纯图片普通下一页旧ack不清新操作；本地失败快照重开第5页；盘已有900不被旧100覆盖。宿主自己的授权revision使无新位置的失败快照被误删，token快照仍混用全局 |
| R4-05 按书损坏/书签 | 部分通过 | 坏A不污染健康B；空白ID、UTF-8 4KiB、摘要256、500上限、完整分页、回执跨书隔离通过。同书短暂拒读恢复仍锁死书签 |
| R4-06 资源 | 部分通过 | 旧书owner2不再撤新书owner2；早read失败40次元数据清零；同path并发和成功共享路径通过。同书准备后段失败仍撤旧可见章共享图 |
| R4-07 搜索 | 部分通过，仍不满足A17 | 热零/稀疏/密集取消、立即重查、hidden br/SVG属性、旧高亮阻断有效。真实块界漏搜、失效导航提交、清洗前后坐标不一致与SVG可见继承仍失败 |
| R4-08 字体声明 | 既定缺陷通过 | 缺ZIP目标、伪MIME、未知算法/XHTML拒绝；准确IDPF及标准字体MIME正例。只证明声明分类和系统字体降级，不宣称解密或字体实际可解码 |
| R4-09 协商/兼容 | 协商通过，矩阵需核对补充 | v2四能力不足时诚实local；R3 v1明确INCOMPATIBLE_PROTOCOL。原生兼容有身份更正，基础打开不等于完整A21 |

普通图片重开、独立DOM Range文字锚点、实际after-DOM新书失败回滚、20次翻页、原内外层链接白名单均有本轮正例。新失败不能把这些正例改写成“全部未修”。

## 3. R5 必须落实的九组返工

### R5-01 · P1/P2 · 偏好过期字段可完成，不能堵住其它字段；幂等记录与容量分开

依据HA4-03/06/07及原A19。正式互斥激活序列在[宿主S01/S05](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/runs/supplement/results.json>)：A字体18已授予尚未提交→A失活/B激活→B字体32落盘→B失活/A激活→A改行距2.2。A把旧font18和新line2.2放进一笔请求，宿主整笔PREFERENCE_STALE，line也不落盘，两个pending持续保留，关闭false。若B的新32被真实拒写，A新的无关字段仍被旧font连带阻塞。S01是每次仅一窗活动的事件替身；第4节另有原生快速交错，已独立确认同类陈旧字段阻塞。两条精确时序分别记录，不混写为同一场测试。

**设计：** 明确字段级结果“已应用/同操作已完成/被更新操作取代/可重试失败”。可保持事务原子性，但必须指出被取代的字段，让客户端完成这些旧操作并用仍有效的原grant提交其它字段；或采用明确的字段级结果。不能永远重发同一必失败组合；也不能给旧值重新发grant让它超越新窗口。被取代、真实写失败和确认丢失必须分别处理，提示与pending状态一致。

[容量C01](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/runs/capacity-ack/results.json>)：4095未完成授权+font28第4096笔实际写盘但扣留ack；新line授权回收该已完成token，原客户端3秒自动重试得到MISSING_GRANT，两个字段卡住。**已写入不等于客户端已确认。** 活跃授权和有界幂等完成凭据应分开管理；明确期限/会话结束/过期操作回收，保护合理重试窗口。连续4097次input不送防抖属压力边界，不能描述为普通用户输入频率；仍应有可恢复的背压，不让最后一个失败grantPromise永久阻塞。

scope目前入口未读，`scope:"book:foreign"`能用global grant写全局。若只支持global，应明确拒绝其它值且不改盘。字段/session/operation校验保留。授权后正常失活仍可冲刷不单独视作越权；值在首次成功保存时绑定与“授予时已冻结输入值”应在契约中准确区分。

**验收：** S01/S05；普通32/2.2；同字段反序提交；跨字段/主题组合；丢ack自动重试；容量边缘回收后重试；未知scope不写盘；所有失败记录实际pending和文件。双独立进程共享目录订单相撞为条件边界：若产品保证单实例，提供依据并明确边界；否则设计跨进程序列。不要把它混作同进程双窗已失败。

### R5-02 · P1/P2 · 位置资格来自原操作，恢复判断使用位置提交身份

[宿主S03](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/runs/supplement/results.json#L207>)是本轮新增P1：原file change打开A revision0→下一页100→GrantInteraction将通用revision变1但position仍null→实际A文件只读，保存失败→换B→解除只读→重开A。客户端因loaded revision1大于snapshot baseRevision0，删除失败快照，画面回offset0，磁盘仍position:null。**没有其它窗口写过更新位置。** 本地C02修复通过不能覆盖此宿主分支。

根因是[ReaderStateStore授权修改通用revision](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-snapshot/ReaderStateStore.cs#L280>)与[resolveFailedSnapshotOnOpen直接比较它](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L375>)。设计应区分位置实际提交版本/操作身份与授权、书签等元数据修订；只有确有更新位置，才按冲突策略取代旧失败位置。保存仍失败/资格过期时保留待确认内容并准确显示状态，不能静默丢弃。可见恢复位置也要符合裁决，不只改存储。

资格快照仍有明确代码缺口：[reader.js:434–439](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L434>)等待snapshot.tokenPromise却丢返回值，随后重新读取全局token；刚赋局部变量便与同一个全局比较不等，分支恒假。受控反序token2先到、token1后到时，新offset200也携token1被拒。**尚未证明原生WebView消息正常会自然反序**，但实现违反已要求的冻结身份。

**设计：** 位置操作携带原book实例/bookId/openIntent、位置、opVersion、tokenPromise及其返回token、sequence、operationId和位置基准；回执不能覆盖新操作资格，不能在await后补全局token。原操作重试沿用其身份；换书/恢复不能静默换序号将旧内容升级为新操作。真正新用户操作可建立新身份；成功、失败、被取代的完成状态分别处理。若增加必需协议字段，能力协商必须同步更新，不能对旧宿主误宣称完整持久化能力。

**验收：** S03实际文件拒写恢复；授权/书签仅改元数据而无新位置；旧100对新900保留新900；同offset跨章/纯图；token反序；旧失败迟于新成功；同书重开、换书时等interact、失效资格。保留普通翻页→改设置与纯图旧ack正例。

### R5-03 · P1 · 收尾冻结最终目标，停止所有输入/导航；成功必须对应已确认状态

[宿主S02](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/runs/supplement/results.json#L98>)：下一页offset100→页码输入9尚待420ms→Closing保存100且扣留回执→仍处关闭等待、窗口尚未Close时，页码防抖跑到offset800→释放旧100回执，客户端ok:true并关闭，modified仍true、盘仅100。这是关闭等待期间发生的全链错误，不依赖假定窗口销毁后JS还能运行。

[Edge D03](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence/results.json>)另证：第2页尚待保存→下一章读取挂起，ready=false/modified=true→finalize，直接ok:true/revision0且本地无位置；释放读取后旧导航仍提交下一章。这里用明确local能力的host消息夹具，是真实Edge/原controller，不是原生C#关闭证据。模块prepare/resources两阶段也证实成功ack后仍定位/高亮。

**设计：** 统一收尾状态机，先按明确政策结算最后页码输入/动画目标并冻结位置/设置，停止pageInputTimer等所有入口与定时器；使open/nav/query/布局资源任务失效。ready=false不能等价于“没有待保存位置”，应保留上一次有效阅读快照。正在进行的flush结束后仍核对冻结目标是否已确认，失败/被取代/超时不能冒充全部保存成功。所有结果完成路径检查finalizing，不仅按钮入口。初始化前1500ms独立期限、严格request/session/source、无修改关闭、真实写失败ack:false均保留。

**验收：** S02、D03；待页码输入/位置回执/偏好grant/偏好save；18在途后新32；读章/换书/搜索/动画中；真正无修改；无响应超时；错/缺关联；初始化前和Ensure挂起。逐例记录冻结目标、最新op、pending、落盘、ack及真实窗销毁时间。

### R5-04 · P1/P2 · 查询/开书意图贯穿导航提交全过程

[Edge D02与截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence/stale-search-document.png>)：START章搜索OLDQUERY并点击，readText挂起；输入NEWQUERY后放开，旧高亮被正确阻止，但正文仍切到OLDQUERY章，spine0→1。提交C05绿灯只检查highlights，结果本身也记录spine1。

[原reader/原Paginator阶段证据](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json>)覆盖prepare与资源等待：改词、关闭搜索、新file意图但arrayBuffer未完成，旧任务仍继续DOM/布局/定位/状态提交。新书已经完整替换后，旧任务则被现有navGeneration正确拦住，两者不能混淆。

**设计：** 建立导航操作对象，携带book/openIntent/nav/query身份及取消状态，贯穿prepare、srcdoc提交、资源等待、layout、goToPage、高亮、资源交接与保存。每个异步边界及副作用前复核；新文件意图发生时就失效旧任务，不等文件读完。文档已提交后失效，要有一致的提交/回滚政策，不能继续定位并把旧状态标为当前成功。导航资源仅由原操作/原book释放。

**验收：** 上述三种失效事件×两个阶段；关闭窗口同样失效；取消后用户再翻页/目录操作最终正确；完整换书已通过的正例不能退化。检查真实正文、页码、活动目录、保存位置和资源，而不只检查高亮为空。

### R5-05 · P2 · 搜索分块保持整串匹配语义及真实计数

[Edge D01](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence/results.json>)中CROSSBOUNDARY实际块界65844，三个章的唯一匹配offset为8、65840、65875，界面只报2处、扫描3/3章，漏掉跨界匹配。[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence/search-boundary.png>)可见仅c0/c2结果。

[模型X01/X04/X05](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json>)还证实NEEDLE真实界65816、1001处其中一处跨界变成1000且truncated:false、跨界emoji/长空白、重复aaa的非重叠计数错误。只测名义65536边界会漏检，因为当前[chunkStart=chunkEnd](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js#L133>)根本没有重叠。

**设计：** 分块推进必须等价于既定整串匹配语义，保留正确的全局offset/length和非重叠扫描位置，并避免重叠窗口重复计数。空白匹配允许的跨度不一定受query.length×4保证，不能仅把现有chunkStart减固定常数就当完成。保留有界工作量让出/取消；扫描未完成、读取失败与完整零结果的提示区分。

**验收：** 每个实际块界前后及跨界；中文、UTF-16 emoji、跨节点/跨空白、重复匹配；普通及跨界1000/1001；超缓存章之后唯一词；冷/热、零/稀疏/密集取消及立即重查。74,001字符查询触及RegExp引擎限制属边界，不能伪称单独证实chunk问题；明确输入限制/错误提示即可，未要求无限长查询。

### R5-06 · P2 · 搜索和显示使用同一有效内容及可见性规则

[Edge D04/D05](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence-v2/results.json>)无时序/故障注入：`START <span style="display:none">HIDDEN</span> TARGET`搜索索引排除了HIDDEN，显示清洗却移除style让它重新显示，TARGET的offset8映射到`IDDEN `。SVG内联visibility例实际高亮HIDDEN。调用同一buildTextModel函数，不代表清洗前后两个不同DOM有相同坐标。

[Edge D07](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/svg-visibility-evidence/results.json>)及[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/svg-visibility-evidence/visible-child.png>)：SVG父g visibility=hidden、子text显式visibility=visible，浏览器真实显示OVERRIDE_VISIBLE且有196.5×28几何，索引为空，查找说没有匹配。这与已声明不支持use引用是不同问题。

**设计：** 先定义清洗后的有效正文与安全的隐藏语义，再让搜索、Range映射、定位、保存锚点基于一致结构。删除style时不能让被索引过滤的文字无声加入显示坐标。正确区分display:none不可由子节点恢复，与visibility继承可被子节点覆盖；虚拟换行规则也要一致。保留hidden br、SVG presentation属性、defs/title/symbol模板与aria-hidden视觉可见策略。use不解析可继续明确声明，不要求借此展开新功能。

**验收：** D04/D05/D07；hidden/style/visibility组合及br；搜索命中内容严格等于实际Range与高亮；搜索后清高亮再保存/改字号/重开，原UTF-16文字位置仍一致。

### R5-07 · P2 · 准备任务不等于已显示任务，失败释放保护活跃引用

[Edge D06](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/new-boundary-evidence-v2/results.json>)：旧章owner1显示shared.png，下一章owner2复用同URL后，注入sectionTitle异常。新catch清理owner2，但_currentTaskId已是2，释放时只查该任务保留集，漏掉仍显示的owner1。最终旧正文、spine0、readytrue保留，当前img.src同一个URL却fetch失败，resources=[]、owners=[1]。已解码naturalWidth仍1，**不宣称当前像素立即消失**。

**设计：** 区分候选准备任务与已提交显示任务。资源释放必须看仍活跃/被显示或回滚保留的全部引用，或用明确引用计数/所有权转移；不可用“最新开始的task”代替“当前有效所有者”。成功提交后再转交与释放旧任务；失败、过期、销毁、回滚都使用原book+owner。原40次早read失败的任务清零和跨书owner2保护必须保留。

**验收：** 早read失败、资源获取后元数据失败、setSection资源等待失败、after-DOM回滚、共享路径并发/反序、旧/新书同owner、销毁中完成。旧可见章所有URL始终可fetch；200含图切章、10换书静置观察DOM、任务集合、create/revoke与进程趋势。

### R5-08 · P2 · 同书暂时拒读恢复后可重新读取书签

[宿主J14](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/host-audit/runs/main/results.json#L926>)：健康原书签keep，localStorage.getItem暂时抛错；恢复读取并重开同书，仍bookmarkCount0/LOCAL_CORRUPT，原健康键值保留却不可见。原因是[_localBookmarkState](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader-state.js#L437>)在重读之前先拒绝旧损坏标记，使load无法清除它。此证据使用Map存储故障替身，不称真实浏览器已拒读。

**设计：** 明确“读取/校验当前原件”与“允许写入”的不同流程；重读不能被旧故障永久阻止。区分暂时不可读、结构损坏、未知版本；恢复后重算当前状态，仍拒读/损坏时继续保护原字节。按书隔离及偏好独立作用域保留。

**验收：** 同书拒读→恢复→重开/列出/新增/删除；损坏未恢复；未知版本；坏A→健康B→回A；字节保护、完整分页、中文/空白ID及容量边界不回退。

### R5-09 · P2 · 候选身份、完整矩阵和报告断言一致

实施方22模型+8reader-flow脚本与R3逐字节相同，没有删改这些断言；但helper身份头遗留R3commit/ZIP常量，实际四个JS散列与R4相符。应改正身份头，不因此否定实际R4执行，也不能让旧常量进入新报告。另四条R3 boundary本轮由GPT补齐。

报告把T03说成use观察、把E03说成use/URI，与实际脚本不符：T03没有use且PASS，E03只测CipherReference的根/OPF fallback策略。smoke中的“A05字号重排”实际对应原A10，不能冒名关闭原A05异步导航；“A13无空闲rAF”不等于60/144/240Hz耗时验证。每个ID回到原任务书定义。

R5提供逐条A01–A25状态、精确被测候选身份、完整脚本与机器结果、真实未覆盖项。所有包内文件与实际加载模块比较；entry相同不代表reader/state相同。原生兼容必须用最终候选DLL，干净解包完整扩展，记录extension.json及关键JS散列；不能只看显示版本或旧截图。每个需要比较落盘的场景保存对应时点的实际状态文件，不只写摘要/散列。本轮supplement-results的identityCorrections全为null，因此实际身份以各单独identity文件核对；R5汇总器不得静默省略/吞掉身份读取失败。

## 4. 原生补充与兼容身份核对

补充已冻结为[57份原始文件](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement>)，原SHA256SUMS列出的56份全部核对通过；清单见[补充身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/native-supplement-identity.json>)。补充报告SHA-256为 `390FEAEE8E04919731F51570D37AAADECCCAC12E9CCD830A0C0B41EDBA4B78CD`。其后只追加了[2份更正材料](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-corrections>)，分别冻结，不覆盖原快照。

**原生设置正反例。** 普通顺序PID50020，A字号18先保存，再B字号32，A行距2.2，最终文件32/2.2，证明已保存的旧字段不会再随A行距一起覆盖B。快速顺序PID50864则失败：[原始日志:50](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/windows/native-fast-A18-B32-A22-raw.log#L50>)的A字体18在02:04:06.883提交，因B已有更新授权遭拒；A行距首次授权07.360，B32于07.538提交并成功；A最终于08.225发送 `{fontSize:18,lineHeight:2.2}`，08.226整笔PREFERENCE_STALE。补充报告记录最终仅32、无行距；冻结材料未附该时点偏好原件，本验收直接确认的是新行距所在事务被拒、未应用。独立全链S01另保留了实际偏好文件。原生由连续滑杆/键盘输入产生多次授权，不把它简化成严格“B落盘之后才开始A行距”的替身序列；但**陈旧字段阻塞新行距已原生确认**。对应[PID与模块身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/windows/native-fast-two-window-identity.json>)为最终9EBA DLL与659A reader。

**真实写失败正例。** PID40224在隔离偏好文件只读时改行距，正常save及关闭重试都WRITE_FAILED；[日志:47–54](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/native-failure/native-write-failure-close-raw.log#L47>)明确host.finalized ok:false、CLOSE_ERROR。提交前后文件散列相同 `6024154CB66AC2D534C563B475BB743AF799711CC8AAE67BB557A8131E5D904A`。这补上了原报告未做原生I/O失败的缺口。原“18回执在途→32→关闭”仍只有独立全链受控正例，原生未成功构造，不冒称已测。

**早期与普通关闭。** 冻结提交正常设置关闭的lineHeight2.1先写盘再ok:true/revision4；早期关闭01:39:19.787→01:39:21.299，约1.512秒超时退出。它们不能覆盖本报告S02/D03失败分支。

| 兼容组合 | 已核对的身份与结果 | 可采用的边界 |
|---|---|---|
| 最终R4宿主＋真c12有书 | [更正身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/compat/finalhost-c12-withbook-identity.json>)，PID57968，9EBA DLL，旧入口88FA…9721A/675行；原生日志legacy ready，截图有旧UI正文 | 基础有书打开通过；原提交7C79 DLL场景退出最终候选证据 |
| 最终R4宿主＋真c12无书 | [冻结身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/compat/r4host-c12-nobook-identity.json>)，PID51988，9EBA与真旧入口 | 已提交无书基础入口；不扩为全部关闭/持久化 |
| 真b978661宿主＋干净0.8.0 ZIP | [更正身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/compat/oldhost-b978661-r4zip080-identity.json>)，PID73980/50636，57CC DLL，reader659A/stateA790/metadata0.8.0 | 基础打开、翻页、诚实local通过；取代原混合metadata0.7.0目录。翻页截图实际为3/9，报告称2/9；只采用基本翻页事实，不推导精确点击次数 |
| 最终R4宿主＋R3 0.7.0 | [身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/compat/r4host-r3client-identity.json>)、原生截图及独立原客户端互配 | 原生有书/local提示；当前运行reader散列34595090…AA3085E确与冻结R3相同；共享index散列本身不够 |
| 真旧/旧、PDF0.3.0 | 旧/旧引用R3更正基线；PDF最终9EBA，截图1/50且有缩略图 | 旧/旧仅沿用不变组合基础证据；PDF基本打开，不等于搜索/多窗口/无书全回归 |

**A08仍部分。** 新a08样本从c1关窗/应用重启后恢复c1和lineHeight2.2；但没有bookmark.add，bookmarks为空，[重启后截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/a08/5-reopen-after-app-restart.png>)实际为破图占位。只认可所示章节/设置恢复，不认可书签同时恢复或有效图片渲染，也不在未排查样本前把破图直接判为产品缺陷。

**A22撤回不成立的“60秒卡住”。** 两本373/255章书的首/中/末、字号、书签、刷新恢复有浏览器证据，非原生测试。GPT核对[实际脚本:74](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-supplement/tools/r4-realbooks.cjs#L74>)发现只等1800ms就取结果并关闭搜索；没有60秒执行脚本或elapsed记录。实施方已在[更正文件](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-native-corrections/CORRECTIONS-A22-A-TABLE.md>)撤回长时卡住断言。采用事实仅为1.8秒时结果0且进度373/373或255/255；后续是否完成及耗时未测，不能列为新增已证搜索缺陷。R5等待明确完成/取消并记录实测时长即可。

A11真正晚资源仍未测；A23只有模块/旧证据，没有本轮原生200次含图切章/10换书与进程趋势；A25干净包实际入口基本运行已补齐。对已有明确反例的A05/A17/A19/A24不能写UNTESTED或PARTIAL来稀释失败；其余仅覆盖子项的PASS也不自动升级为整项通过。

## 5. 原 A01–A25 标准与 R5 最低交付

以[原始验收矩阵](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md#L371>)为唯一ID定义。R5逐项回交，不得把“脚本全绿”或补充基础截图写成整项通过。

下表为按原标准及R4返工要求汇总的总验收状态，优先于实施方子测试标签。PARTIAL表示已有正例但不足以证明整项，不能反向解释为已证产品失败；FAIL均有本轮明确反例。

| 原ID | R4综合状态 | R5保留/补齐内容及依据 |
|---|---|---|
| A01 | PARTIAL | TOC/spine分离及片段有正例；补齐辅助导航并存、连续正文完整性与所有片段的对应证据 |
| A02 | PARTIAL | NCX嵌套及无目录兜底有正例；EPUB3嵌套、前置内容可访问分别列明 |
| A03 | PARTIAL | 片段、一次编码和SVG fragment正例保留；跨章脚注返回、真实SVG显示仍需完整链 |
| A04 | PARTIAL | 旧投喂不抢占URL书已测；三条路径在解码前去重、新宿主不重复读取/编码/分配分开证明 |
| A05 | FAIL | Edge D02及模块分阶段证明旧查询/开书意图后仍提交；保留慢章后快章等已过正例 |
| A06 | FAIL | 同书准备后段失败损坏旧图引用、同书拒读恢复锁死；坏文件可读恢复/after-DOM新书回滚正例保留 |
| A07 | PASS | 同源刷新和独立文字Range恢复已有本轮正例；后续位置重构必须回归 |
| A08 | PARTIAL | 原生c1/行距恢复已证；有效图片与书签同时恢复、文字/图片各自关窗及完整重启补齐 |
| A09 | PARTIAL | 同内容改名共享有正例；同名同大小不同内容的状态隔离单列，不用同一用例代替 |
| A10 | PARTIAL | 文字、长段、emoji、高亮、纯元素锚点正例保留；字号/行距/宽度/侧栏/窗口组合按实际内容验证 |
| A11 | UNTESTED | 本轮没有真正晚于ready/恢复意图的图片与字体整链；模块late timer不替代真实晚资源 |
| A12 | PARTIAL | 普通20次连续翻页已过；首/末边界、反向输入及跨章按实际页数完整回交 |
| A13 | PARTIAL | 无空闲rAF已证；补60/144/240Hz或可控时间源耗时，不能以idle代替 |
| A14 | PARTIAL | 选区不翻页有正例；复制/右键/链接/输入编辑/阅读快捷键分别记录 |
| A15 | PARTIAL | 520/760/1400视口有正例；100/150/200%显示缩放及面板可达/行尾截图未齐 |
| A16 | PARTIAL | 事件清洗/sandbox与内外链接白名单正例保留；嵌套框架、冲突ID、宿主接口隔离完整回交 |
| A17 | FAIL | Edge D01/D02/D04/D05/D07；真实块界、Range、可见性与全过程取消均须修复 |
| A18 | FAIL | 同书书签拒读恢复失败；普通去重/分页/上限及旧回执跨书隔离已过，原生重启同时恢复仍缺 |
| A19 | FAIL | pending-A18原生及独立互斥激活S01反例；普通32/2.2通过。补同书位置、两书签、迟到关闭与新交互接管全时序 |
| A20 | FAIL | 实际文件拒写后失败位置被授权revision误删；真实WRITE_FAILED→ok:false及原件保护正例保留 |
| A21 | PARTIAL | 身份更正后基础组合可用；四种新旧/有书无书及PDF查找/多窗未齐，旧旧只引用不变基础证据 |
| A22 | PARTIAL | 两本373/255章浏览器首中末、字号、书签、刷新有正例；有效插图样本及相邻A17搜索完成测量补齐，不上传原书；60秒断言已撤回 |
| A23 | PARTIAL | 模块200次含图切章、10换书资源有界已过；补同页DOM/URL/任务和实际进程趋势及静置 |
| A24 | FAIL | S02、D03及两阶段finalize仍有旧任务/未保存成功回执；早期1.512秒退出及普通关闭通过 |
| A25 | PASS | 干净最终ZIP在真实旧宿主入口打开/翻页、诚实local；只判包装完整性，不据此宣称全部新宿主持久化通过 |

验收完成条件是已确认缺陷关闭且原矩阵证据完整；不要求把可控故障都伪装成自然发生的原生故障。对需要隔离注入的交错，保留原业务全链与实际文件作为正确性证据，再用真实宿主确认入口、加载代码和正常生命周期。没有制造成功的原生时序写明未覆盖，不以无效操作序列反复截图凑通过。

## 6. 实施与再次交回方式

先提交简短状态设计：字段操作完成状态、位置提交版本、关闭冻结目标、导航提交边界、统一有效文本、资源引用所有权。随后在R5新分支实现，先解决R5-01/02/03数据与关闭问题，再完成04–08；09与回归同步。无需用户逐步确认，不要改R4候选“让旧证据变绿”。

新增DeepSeek实施回报-R5.md、candidates-r5、r5-evidence及独立宿主构建/运行目录。回交精确提交、ZIP/DLL和所有运行模块散列，新增版本与协议能力策略、完整A矩阵、原输入→原消息→实际文件→重开可见内容的证据链。每条失败修复同时保留正常路径与交错反例；故障注入、替身、超时、压力/条件边界明确说明。

旧探针R03访问已删除的textNodePath，不作为产品失败；本轮B01独立DOM Range替代。B14旧判据只看modified和旧存储键，没检查failedSaves，不作为“快照丢失”证据；本轮真丢失依据宿主S03，二者不能混用。测试没有证明的场景继续标PARTIAL/UNTESTED，不用声明边界代替完成已要求的功能。
