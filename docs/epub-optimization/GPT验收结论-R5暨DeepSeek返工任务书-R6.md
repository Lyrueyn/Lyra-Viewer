# EPUB R5 独立验收结论暨 DeepSeek R6 返工任务书

**结论：R5 不通过完整验收，不能作为完成版发布。** 前轮的主要反例已经修复，但资源等待后段仍会留下“新正文、旧章节身份”，关闭时还会把有效位置写成空锚点，或丢掉未完成动画的目标，然后返回成功。另一条原客户端→原 C# 链证明：A 书保存失败后，B 书保存成功能掩盖 A 的失败并让关闭返回成功。实际浏览器另确认 SVG 命中文字消失、CSS 可见性与索引不一致；原生补测确认 v2 扩展被 v3 宿主加载层挡住。

本文是统一 R6 交接依据。继续执行“GPT 设计/独立验收，DeepSeek 实施/提交”的既定分工。继承[原始任务书及 A01–A25](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/EPUB扩展优化-DeepSeek实施任务书.md#L367>)和[上一轮 R5 任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R4暨DeepSeek返工任务书-R5.md>)；PARTIAL/UNTESTED 是证据状态，不是取消原要求。先修数据正确性与收尾，再补齐交互、兼容与完整验收。保留本轮原件，不在原结果上重跑覆盖。

## 1. 被验对象与证据边界

| 项目 | 本轮冻结值 |
|---|---|
| 扩展 | 0.9.0；提交 `76ced4c17efa4526fbc8c00e3fff5261959a7fe7`，冻结时分支 r5、工作树干净 |
| 候选包 | [0.9.0 ZIP](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/candidates-r5/lyra-ext-epub-0.9.0-candidate.zip>)，95,702 字节；SHA-256 `60CC97BF2721BB0239122275B307833D871548C985A56068E649F3D3A1517B80` |
| 实际宿主 DLL | SHA-256 `202954E5930FCDC7A50BD96975B84A12CBF7AA4375AC7334832D28A45F5F9480`；两个候选运行目录 DLL/EXE 成对核验 |
| 身份总账 | [identity](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/identity.json>)：11 个包内文件与源码/候选运行目录相同，4 份宿主源码与提交 manifest 相同；86 份原提交文件冻结，85 项提交清单逐项验证 |
| 原生补充 | [补充报告冻结副本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/DeepSeek补充测试-R5.md>)：47 文件、45 清单项；[addendum](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-addendum/DeepSeek补充测试-R5-addendum.md>)：10 文件、9 清单项，均通过散列核对 |
| 前轮保护 | [270 项前轮完整性](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/prior-integrity.json>)及[23 项 R4 独立模型交付核验](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/prior-model-integrity.json>)通过。实施方曾误写 R4 输出；目前恢复后的原件与冻结副本一致，不能表述为“从未修改” |

证据分四层，不能互相冒名：①主代理亲跑真实 Edge、原模块、实际隔离 localStorage，资源/File/rAF 门控明确披露；②独立原 JS→原 C# 桥，原存储真实隔离文件，WinUI 事件/传输/几何是替身，不是运行候选 DLL；③模块/jsdom 与 Node Blob 测试；④实施方运行的原生候选，GPT 核对身份、日志、实际文件与截图，不冒称 GPT 亲自操作原生窗口。审计未实施产品、安装依赖、部署或修改用户书库。

两个专项均已结束并收齐后台任务：[宿主报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/宿主状态通信独立验收-R5.md>)为 **62 条：49 PASS / 8 FAIL / 5 OBS，0 probeError**；[模型报告](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/model-audit/解析搜索资源导航独立验收-R5.md>)为 **117 条：89 PASS / 19 FAIL / 9 OBS**。失败断言有同一缺陷的多场景和重复层级，不能相加声称独立缺陷数量。宿主 125 个输入、模型 137 个输入前后散列一致。

主代理[浏览器总账](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/browser-summary.json>)共 61 个观察：49 个既有观察中 47 个适用回归通过，R03 的旧 `textNodePath` 探针错误及 B14 忽略 failedSaves 的旧判据继续排除；9 个新增场景为 1 个对照 PASS、8 个 FAIL；另有 2 个正常慢图末页正例和 1 个未建立目标覆盖的 lazy 图尝试。后面三项不能证明“ready 之后才完成”的图片重排安全。

## 2. R5 已确认修复，R6 必须保留

| 范围 | 独立确认的结果 |
|---|---|
| 跨窗字段交错 S01 | 互斥激活、A18 已授权未保存→B32→A 行距 2.2，原链实际盘为 32/2.2；font superseded、line applied，pending 清空，关闭成功。没有用“先保存完 A18”代替该顺序 |
| 页码输入收尾 S02 | 第9页输入防抖尚未触发即关闭，原 JS→原 C# 首次保存就是 offset800；扣住 ack 时不再漂移，释放后才成功 |
| 失败位置重开 S03 | A100 实际文件 ReadOnly 拒写→原 file-change 换 B→解除故障→原 file-change 重开 A，盘与可见位置恢复到100，failed 清空；不是单独 C# load 或 fake-host 的拼接推论 |
| 授权与本地恢复 | J11 token2先回/token1迟回，最新200使用token2；J14同书临时拒读后健康原件可见；单字段 C01 在4095个pending附近、写盘扣ack并回收token后重试成功；非法scope被拒绝且不改盘 |
| 搜索分块语义 | R4 D01 跨窗口漏命中已修复；新增86组整串 regexp 参考比对全通过，包括实际65536/131072边界、UTF-16/emoji/中文、4096单位查询、超长空白、非重叠语义、1000/1001结果边界 |
| 可见性和资源正例 | R4 D02–D07 的原观察均通过；HTML/SVG普通隐藏边界、SVG子元素恢复visible、同书失败准备不撤销旧共享图引用已修复。模型原65条完整保留为58 PASS/7 OBS、无FAIL；其中R3原34条仍31 PASS/3 OBS |
| 资源模块趋势 | 新增40次资源后准备失败、40次候选取消、200次图切章、10书销毁，Node原生Blob字节验证通过，最终create302=revoke302；只证明该层引用管理，不外推原生进程无泄漏 |

R4 D05 的旧判据只检查包裹文本，本轮发现它不足以验证 SVG 实际绘制；D02 的旧判据也不足以验证 DOM 已提交后的完整状态。因此“原反例全绿”不能替代后述新边界。

## 3. 必须修复的确定问题

### 3.1 已提交正文后的取消、失败与关闭，破坏章节/位置的一致性（P1）

真实 Edge 的 [E01/E02 完整结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/new-stage-evidence/results.json>)及[精简状态](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/new-stage-evidence/summary.json>)：从 c0 的有效文字位置进入搜索结果 c1，在原 `_waitForResources` 阶段门控；此时新 DOM 已出现而尚未完成排版。真实改词/关闭搜索后释放门控，得到 `state.ready=true, spine=0, prepared.index=0`，但 `Paginator.ready=false, sectionIndex=1, pageCount=1`，正文仍是 c1。再点击下一页未前进，真实 localStorage 写入 **c0、locator:null、fraction0**。显式重新章导航能恢复，所以不夸大为永久不可用。

同一阶段直接 finalize：此前稳定锚点 textOffset2506/pageIndex4 被替换为 c0/null/fraction0，`host.finalized ok:true`。这是实际浏览器及本地存储证据。宿主专项 [L01](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/runs/close-boundary/results.json>)加载原 Paginator 的 setSection/资源等待，doc1/fonts.ready 挂起，原 C# **真实文件**也由应保存的 c0/100变成 c0/0并成功关闭；几何替身边界详见专项，不把两层拼成已亲跑原生重开。

模型还证明同书资源等待/after-layout失败、new-file意图取消会出现同类混合状态，并撤销仍在当前新正文中引用的候选独有图片 URL。旧共享图片修复不能覆盖这个分支。根因见[导航取消路径](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/candidate/reader.js#L999>)、[先替换文档的分页器](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/candidate/paginator.js#L213>)和[收尾刷新稳定位置](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/candidate/reader.js#L1492>)。`ready=!!doc` 不是已提交、可用正文的证明。

### 3.2 动画关闭、跨书失败账本与迟到任务尚未收口（P1）

- **动画目标丢失。** 真实 Edge E06 用可控rAF暂缓首帧，原下一页已接受 target1/scroll0；finalize先cancel再settle，使 settle 看不到pending动画。落盘 locator.textOffset15，虽然pageIndex字段写1，真实reload仍回page0。同场景pagehide对照先settle，offset659，reload正确page1。[原浏览器探针](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/review-new-stages.cjs>)和[宿主E05实际文件](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/runs/extended/E05-close.json>)相互印证；这是受控动画边界，不推断日常发生频率。
- **A失败被B成功掩盖。** 宿主 E03：A100真实拒写，换B后B100保存成功；关闭时A100仍在failedSaves、A盘position:null，却因B清掉全局saveFailed/modified而回ok:true。没有新位置取代A。[关闭时A文件](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/runs/extended/E03-A-at-close.json>)与[原始链结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/runs/extended/results.json>)为依据。S03“重开A可恢复”不能覆盖“没重开A就退出”。
- **确认后仍有异步收尾。** Edge E07：真实file input字节挂起→finalized true→释放字节，原open流程继续，ready从true变false并出现“打开 EPUB 失败：正文载入失败”。页面为观察而保持存活，不声称原生窗口销毁后JS仍执行。模型[迟到资源结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/model-audit/r5-late-cancel-results.json>)还证明原2500ms资源等待超时后的load监听，会在取消/finalizing后经120ms定时器再次layout；image.complete/load为明确替身。

### 3.3 完成凭据遗漏 superseded 字段，压力恢复仍不完整（P2，明确条件）

宿主 [K01](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/runs/completion-mixed/results.json>)：预填4094个未完成grant；A sepia一次产生theme/bg/fg，B更晚自定义bg覆盖theme/bg；A保存实际fg applied、theme/bg superseded，扣住真实ack。A再改line2.2，已完成token被回收；原客户端重试同笔却报 **FIELD_NOT_GRANTED theme**，四字段仍pending、line没落盘、关闭false。原[Store完成缓存](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-snapshot/ReaderStateStore.cs#L550>)只记applied/idempotent，漏记已完成的superseded字段。实际运行723ms，客户端3秒重试由可控时钟触发；不是普通人自然输入4094次的证明。

C02仍是4097个未让防抖执行的原input后GRANT_LIMIT，最后意图绑定失败grantPromise而不能恢复。它诚实报错、没有假成功，但不满足上一任务书的压力恢复要求。单字段C01通过不等于多字段凭据及无可回收项的压力情形通过。

### 3.4 位置操作身份与本地未知版本保护仍有缺口（P2）

宿主 E01实际拒写A100后，没有新位置意图，换B前再次flush同一100却生成新的sequence/operationId。之后重开能恢复100，因此只判**原操作身份冻结契约失败，不捏造数据丢失**。根因是[每次flush重新建身份](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/candidate/reader.js#L431>)。宿主目前不读取operationId，幂等实际依据session/token/sequence/value；不要把未执行的字段绑定写进结论。

本地 E08 的原客户端在Map localStorage边界上把 `version:999, revision:7, items:[keep], futureData:retain` 当健康书签读出，添加后重写成无version/无futureData的当前格式。见[宿主专项第6节](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/宿主状态通信独立验收-R5.md#L92>)及[本地读取/写入](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/candidate/reader-state.js#L453>)。宿主未知版本保护和本地坏JSON保护均不能替代此项。

### 3.5 搜索的实际显示和错误提示仍不正确（P2）

- **SVG命中文字消失。** 无故障注入的Edge E03：`VISIBLE TARGET` 搜索TARGET并点击，DOM原字符串仍在，但正文只显示VISIBLE。SVG文字长度210.84375→98.890625；命中包裹节点为XHTML namespace，clientRects=0、宽0。[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/new-stage-evidence/svg-after-highlight.png>)已核对。把HTML mark插进SVG text不能算成功高亮，旧D05只核对textContent漏掉此问题。
- **重复visibility声明取错有效值。** Edge E05与模型独立一致：`hidden;visible`的TARGET实际可见却搜0；`visible;hidden`的HIDDEN实际隐藏却搜1。[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/new-stage-evidence/css-visibility-order.png>)和[模块结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/model-audit/r5-search-visibility-results.json>)保留。正则拿第一条，CSS取最后有效声明；important清洗与SVG presentation属性也必须按清洗后的有效语义统一处理。
- **4097单位查询未显示限制原因。** 模块诚实返回QUERY_TOO_LONG、零读取，但真实UI只说“当前结果上限/未完成范围…已扫描0/0章”，没有长度限制或缩短提示。4096及随后短查询正例通过，不能把该条说成搜索算法无法恢复。

内部隐藏节点夹在A/B之间时，连续Range.toString为AHIDDENB、隐藏节点也可能有mark；父隐藏样式仍保留，模型AB/命中坐标正确。该项单列语义观察，**不据Range字符串或所有mark.textContent断言隐藏文字实际被高亮**，也不与上面实证SVG消失混写。

### 3.6 v2客户端降级后，原生正文仍被加载层挡住（P2兼容）

独立原链P01观察到v2 hello超时诚实local，但生命周期被忽略；新增原生证据将后果闭合：[PID43440 identity](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-addendum/v2interop-identity.json>)为最终R5宿主DLL＋完整冻结R4 0.8/v2包。[日志](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-addendum/v2interop-host.log>)确有v2 app.ready/hello/book.ready而无REPLY；实施方3/9/12秒截图仍显示加载遮罩，22秒进入[“仍在加载这本书”超时态](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-addendum/05-22s.png>)，GPT已核对该图，正文不可见。时间点来源为实施方记录，不从截图文件名独立计时。不能宣称local正文可用，更不能把真c12或R3/v1成功外推到v2。

## 4. 原生补充、条件观察和未完成证据

**A08正常路径取得合格正例。** 最终R5候选的有效图片章、实际bookmark.add、行距保存、关窗重开及完全退出重启已有日志/状态/截图。组合文字例关窗前[实际state](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/native/a08/09-combined-state-46fbd875a7978c82b7eecbb775844269176cf1abba525090fda9ba65b4ad7925.json>)与[重启PID39272 identity](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/native/a08/10-combined-app-restart-identity.json>)是textOffset1758/pageIndex2，书签仍在；[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/native/a08/10-combined-app-restart.png>)显示第3/14页、paragraph12。单独text-anchor例的3072不能混入组合例；addendum已更正。图片重启[截图](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/native/a08/06-reopen-after-app-restart.png>)显示c1有效红图。此结论限正常稳定关闭及合成书，不抵消资源阶段/动画关闭失败。

**A19不将正确拒绝旧位置误报为丢失新位置。** 同书两窗实际两个bookmark.add，磁盘保留两书签；[关闭原日志](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/native/a19/a19-samebook-close.log#L54>)中第二窗ack:false前明确STALE_INTERACTION。不能为追求两窗全true而给旧快照重新授权覆盖新位置。不同书原生最终32/2.2成立，但精确中间文件不齐，也不能把先完成A18保存的记录冒称未保存A18交错；S01的严格顺序证据来自独立原链。

**A23补充仍不足。** [提交脚本](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/tools/a23-stress.cjs#L28>)及[结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-native-supplement/a23/results.json>)确实记录浏览器DOM/URL/任务/JS heap；但200次循环从spine0到199，只完成199次跨章，最后动作在末章；等待异常被catch吞掉。10次“换书”实际是10次page.goto整页导航，不能证明同一阅读器上下文内openFile/换书销毁链。图片独立Edge解码为有效1×1（[核验](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/supplement-image-decode.json>)），不应诬称破图；但单个共享小图、页面重建和1秒静置不能替代真实含图负载、同上下文换书与原生进程趋势。

**A11覆盖限制。** 亲跑3.7s、8.5s慢图及lazy尝试，ready采样时incomplete均0，末页正确。这证明正常慢载，不证明ready后的真实迟到几何变化。[lazy结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/late-lazy-evidence/result.json>)明确coverage:false、defect:null；不能把名为late的脚本当该分支已测。模型的迟到load取消反例仍有效，但只是另一层、另一条件。

**真实书计时可承认其本轮范围。** [373/255章抽检结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/submitted-evidence/realbooks/r4-real-books-results.json>)分别记录4030/2678ms，终态显示373/373、255/255已扫描，1000个保留结果/1236+命中与591结果；本轮计时与最终UI存在。不是R4曾撤回的“60秒卡住”断言，也不泛化为性能保证；不复制或上传原书。

其余条件观察保留而不冒称已发生的常规缺陷：两独立进程共享目录order1冲突（产品是否允许此运行方式未证实）；completion2048容量淘汰例实际119117ms且JS时钟暂停，不是自然3秒重试失败；偏好grant等待串行占住关闭预算而独立位置没发出，是明确设计风险/手动deadline观察，没有假成功。详见[宿主第7节](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r5/host-audit/宿主状态通信独立验收-R5.md#L108>)。

## 5. R6 实施任务：先建立一致状态，再处理边界

### R6-01 · P1 · 提交中的文档与可保存的正文必须分开

在实现前写清状态设计，至少区分“最后已提交且可读的view”和“正在准备/加载/排版的navigation”。已提交view应绑定同一book/openIntent、spine/path、prepared owner、文档身份、分页状态和锚点版本。取消/失败不得把两者的字段混用。

可以采用离屏候选frame完成后原子切换，也可以采用有完整恢复验证的同frame方案；选择一种可证明的政策。若同frame已换DOM，必须恢复旧view或完成合法的一致提交，不能仅stop后续goToPage/highlight并设置ready=true。过期操作的cleanup只释放自己未提交的资源，不能改较新操作的busy/ready/遮罩。当前可见DOM所有资源的存活引用必须覆盖其实际显示期间。

稳定位置只能从已提交、分页就绪、身份一致的view更新。资源等待期间保留旧稳定位置，收尾不能重新读取候选DOM污染它。测试必须分别卡住prepare、iframe/load、resources/fonts、layout之后，并验证input/searchClose、新file意图、更新导航、异常、finalize。每例除检查旧定位不执行，还要核对DOM/spine/prepared/page/ready/owner，继续翻页保存、关闭及重开；至少保留E01/E02与宿主L01的完整闭合验证。

### R6-02 · P1 · 关闭按未完成用户操作账本结算

先阻止新输入和新open意图，结算待页码与动画目标，再取消相关调度并冻结可保存快照。不要在settle之前清掉唯一的动画pending标记；推荐明确记录目标与是否已结算，使正确性不依赖某个raf ID恰好非零。冻结后禁止File.arrayBuffer、parse/open、资源load/font、排版定时器和旧导航继续改变页面/稳定快照。

以每个book/operation的状态计算关闭结果，覆盖当前位置、failedSaves、设置及尚未结束的实际用户保存请求；B成功不得清除A失败。保持原操作的授权与身份，不借收尾重新抢占优先级。只有相关操作均有持久化完成证明，或有**真实较新已落盘位置/字段**的明确superseded证明，才可整体成功；单纯interact/metadata revision增长、STALE_INTERACTION或错误被清空都不是这个证明。仍未完成必须返回失败/部分失败并留下可见诊断，不声称已保存。

处理宿主1500ms与客户端请求3000ms的预算关系；可独立保存的位置不应因偏好grant等待而根本未发起。选择有界并行/分组预算等实现并记录实际期限，不通过延长到无限等待或提前true掩盖失败。保留页码9、18在途→32→close、无改动、早期WebView创建关闭、原文件写失败等已通过正例。新增A失败→B成功→不重开A直接关闭，故障仍在/已解除两组；以及动画不同进度关闭、保存/ack不同阶段关闭、资源阶段关闭后的真实重开。

### R6-03 · P2 · 完成凭据保存整个授权范围与结算结果

已完成grant的独立凭据必须包含原授权字段全集和每字段终态，包括superseded；活跃grant回收后，合法重试必须重放完整完成结果，不能因被覆盖字段消失而变成FIELD_NOT_GRANTED。对同token的未授权字段/不同值重放仍严格拒绝，不能为修幂等扩大scope。

处理无可回收完成项时的背压、未提交grant合并或队列恢复，保证最后用户意图仍可正确结算。恢复不得给旧字段换更大优先级去覆盖别窗新值；需要区分真正的新用户意图与原操作重试。定义完成缓存容量、最短重试保护期及会话回收政策，并实测容量边界。必须复跑单字段C01、多字段K01、全部superseded、部分applied/idempotent/superseded混合、实际写失败、扣ack后再改独立字段、4097压力恢复。

### R6-04 · P2 · 在操作产生时固定位置保存身份

在接受用户位置意图/生成其待保存快照时创建不可变操作包，而不是每次flush创建。至少固定bookId/openIntent、原tokenPromise结果、sequence、operationId、opVersion及位置内容；同一失败操作跨换书前flush、重开、timeout重试都沿用身份。新用户交互才创建新操作。

写清宿主实际幂等键和冲突规则；若operationId作为协议承诺就实际校验/回传，否则明确其仅为诊断关联，不能拿它证明宿主绑定。保留“只有成功位置提交增长positionRevision、书签增长metadataRevision、interact不伪造新位置”的R5修复。用原JS→原C#记录每次完整操作包、实际盘及重新可见位置；不得只比最终offset相同。

### R6-05 · P2 · 本地格式未知时保护原字节

为位置、设置、书签本地格式分别确定支持的旧格式/版本及结构校验。明确可迁移旧格式正常迁移；未知版本/结构不能因JSON可解析而视为健康空表，更不能写掉version/未知数据。错误可见、读写受保护，恢复为合法原件后能重新验证并恢复操作，不留下永久corrupt标记。

在隔离真实localStorage补version999、合法旧数组/对象、损坏JSON、拒读后恢复、写失败；分别核对读回、添加/删除/位置/设置保存和原字节保持。保留健康同书恢复及书签分页/数量/UTF-8字节上限正例，不写用户数据。

### R6-06 · P2 · 模型遵循清洗后的显示，高亮遵循渲染命名空间

把保留样式解析成确定的有效声明，统一display/visibility继承、最后有效声明、important处理及SVG presentation属性优先级；冷搜索模型和展示模型使用同一清洗结果，但还必须与浏览器实际可见内容相符。补重复声明、无效声明、important、祖先display:none、visibility子级覆盖的HTML/SVG组合，不能只比较两份模型彼此相同。

SVG文字高亮采用有效SVG节点/受支持的绘制方案，保持实际文字及几何；不能向SVG text塞HTML mark导致命中消失。验证点击前后可见文字、SVG长度/bbox、目标页内可见性、高亮清除、缩放/重排与后续锚点。HTML跨标签、内部隐藏节点及纯图页仍须回归。4096 UTF-16单位的边界和trim规则应明确；QUERY_TOO_LONG在UI显示具体原因和可恢复提示，缩短后立即正常查找。保留86组整串等价与冷/热取消测试。

### R6-07 · P2 · 版本降级必须完成安全的可用生命周期

补齐v2与v1/真旧端的生命周期兼容：能力不足可以拒绝持久化并降级local，但当前受信session/source/loadId的ready/error必须被正确承接，加载层不能永久遮住可用正文。只接纳约定的兼容生命周期，不顺带放宽旧持久化命令的安全校验。不能简单在NavigationCompleted无条件隐藏加载层，也不能接受外源/旧session的ready。

重新执行真c12、R3/v1、R4/v2和新客户端/新旧宿主的有书、无书入口；R4/v2→R6必须用完整旧包和最终宿主DLL重测，而非只换一个index文件。保留PDF0.3基础打开/关闭/多窗兼容；分别核对local提示、正文可操作、加载层、关闭回执及磁盘行为。

### R6-08 · 生命周期和性能证据必须触达目标分支

A11记录resource request、ready、load/decode、layout的实际时间与阶段；至少构造真正ready之后资源就绪/几何改变的浏览器场景，核对首个可见内容或明确末页意图、静置后不抖动。若采用故障/时钟/资源门控，说明门控位置和未替换的业务路径；没有达到目标阶段就标未覆盖。另补字体、横竖图与取消/关闭交错。

A23使用最终候选，完成**200次确实成功的切章**，不要把循环次数/末章无动作当成功数；每步断言目标章节及ready，失败不能吞掉。随后在**同一阅读器上下文**用真实换书入口连续换10本，记录旧book销毁、DOM、有效图片解码、live URL/task/pending、静置趋势。另给实际WinUI及相关WebView2进程内存趋势、采样时刻/负载/基线/回落；JS heap不是进程RSS。保留浏览器与模块正例，但不要拿整页reload替代换书链。

### R6-09 · 全量验收、候选与交付

按下一节完整A表逐子条件执行。复用旧脚本时复制到新目录，审查输入/输出路径；保留有效oracle，修正过时oracle时单独记差异和原因。不要改断言来迎合坏结果，也不要把模型替身冒名为原生。新增状态设计文档应解释上述view提交、操作账本、字段凭据、版本策略及失败回退，避免继续用互不一致的布尔标记拼接修复。

只在新的R6证据/候选目录写入。交付实施报告、逐项A结果与未完成原因、可复跑探针及原始消息/文件/截图、完整manifest和SHA256SUMS；明确哪些清单元数据不自包含。附commit/分支/clean状态、ZIP每个包内文件hash、最终运行DLL/EXE及真实加载模块hash、隔离数据根与进程身份。旧代码互配要有其真实commit/完整包/模块hash。完成后通知GPT独立验收；未通过前不发布、不安装给用户、不覆盖旧审计原件。

## 6. A01–A25 当前综合状态与 R6 必补项

本表为GPT综合结论，不照抄实施方标签。PASS限已明确证明的原验收场景；新候选仍要回归。原生正常A08不代表所有关闭时机通过。FAIL与PARTIAL分别表示已证实违反要求、尚未覆盖全部要求。

| ID | R5综合状态 | R6保留/补齐 |
|---|---|---|
| A01 | PARTIAL | 正文spine正例保留；补目录遗漏正文、辅助导航分离、同章各片段完整定位 |
| A02 | PARTIAL | NCX/无目录正例保留；补EPUB3深层嵌套及前置正文 |
| A03 | PARTIAL | 路径/片段正例保留；补跨章脚注返回原位置、完整SVG资源 |
| A04 | PARTIAL | 三条旧传书入口同时到达的解码前去重；新宿主不重复整书读取/编码/分配 |
| A05 | FAIL | 资源阶段取消留下新DOM旧身份；按R6-01验证最后意图与全状态一致 |
| A06 | FAIL | 资源/after-layout故障处理仍留混合状态；补失败后的可用状态、重试、手动选择/拖入及较新任务保护 |
| A07 | PASS | 原真实Range/刷新恢复正例；继续保持长段内偏移 |
| A08 | PASS（正常稳定路径） | 已有最终候选原生重开/重启、文字/图片/设置/实际书签证据；R6重跑并另补异常关闭位置 |
| A09 | PARTIAL | 同内容改名共享保留；补同名同大小不同内容隔离 |
| A10 | PARTIAL | 文字/图片锚点正例保留；补字号/行距/宽度/侧栏/窗口、长段重复句emoji高亮的组合 |
| A11 | PARTIAL | 慢载后末页正确；真正ready后资源/字体/横竖图重排仍缺，不能引用coverage:false当通过 |
| A12 | PARTIAL | 补实际可用页面下20次快速/反向/边界连续输入，断言无重复跨章 |
| A13 | PARTIAL | 补60/144/240Hz或受控时间源，终态静置1秒无遗留帧循环 |
| A14 | PARTIAL | 补拖选、复制、右键、链接、输入方向键与阅读快捷键的完整互斥 |
| A15 | PARTIAL | 补520/760/1400px × 100/150/200%缩放的可达性和截图 |
| A16 | PARTIAL | 既有危险scheme/脚本隔离正例保留；补事件、嵌套框架、冲突ID及宿主接口隔离完整合成书 |
| A17 | FAIL | DOM后段旧搜索、SVG消失、重复visibility及超长查询提示，按R6-01/06闭合 |
| A18 | PARTIAL | 书签持久化/分页/上限正例保留；补去重/删/重排跳转/重启的完整组合 |
| A19 | PARTIAL | 同书两书签、旧资格拒绝、原链严格设置交错已证；补原生固定时序/中间文件与新交互接管，不重新授权旧快照 |
| A20 | FAIL | 跨书失败目标被关闭遗漏、本地未知版本可覆写；补异常退出/损坏/拒写的原字节保护 |
| A21 | FAIL | v2→R5原生加载层不消失；补版本矩阵、有书/无书与PDF0.3完整相关入口 |
| A22 | PASS（提交抽检范围） | 373/255章首中末/字号/书签/重开及含图合成书正例；最终R6重测，不上传原书 |
| A23 | PARTIAL | 模块与浏览器有界正例；补200真实成功切章、同上下文10换书、有效含图负载和原生进程趋势 |
| A24 | FAIL | 资源/动画/跨书失败关闭及迟到File/load链，按R6-01/02/08验证停止、释放与正确落盘 |
| A25 | PASS | 候选包11文件/真实入口正例保留；最终R6空目录完整包再验证 |

## 7. 实施顺序与完成门槛

1. 先提交R6状态设计，随后实现R6-01/02；用原Edge＋原JS→原C#闭合资源、动画、跨书失败三个P1，不只检查是否发出ack。
2. 再完成凭据/操作身份/本地格式，以及可见模型、SVG高亮和兼容；每修一组保留此前通过的回归，避免把修复拆成更脆弱的if/布尔组合。
3. 完成A11/A23及A表剩余项目，构建一个明确的最终候选；证据必须指向该候选。必要的原生可控门控可用于构造顺序，但要公开注入点，不能改业务算法代替被测行为。
4. 自检报告与实际数组计数、阶段、源码身份和散列，然后交给GPT。没有故障/时序证明就记录未完成，不以“本轮边界项”自行缩小原范围。

R5提交的文字差异已区分处理：原SHA256SUMS实际为 `BBD2060FF760C9E7B78BB4AAB0151A1F54A9310F85321D3CA9DCD9F2911AFC88`，此前BAA是笔误；真c12本轮PID73288/03:16，57968属于旧运行；宿主提交results数组实际6项而报告称7/7；模型部分旧身份头不能替代实际模块hash；A08组合offset已由addendum更正。这些不用于否认已有正确执行，也不得继续混入R6报告。

**完成条件仍是原功能完整实现、确定缺陷关闭、所需原生证据齐全，并通过独立复验。当前原件、有效正例和失败证据一起保留，下一轮从本文件接续。**
