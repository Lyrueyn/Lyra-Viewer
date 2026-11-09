# EPUB R4 解析、搜索、可见模型、资源、锚点与导航回滚独立验收

**本专项不通过完整验收。** R3 的 34 条独立断言在 R4 原候选上复跑为 **31 PASS / 0 FAIL / 3 OBSERVATION**，原来六条失败断言均转为通过，全部旧正例保留。新增验证发现：分块搜索在真实块界漏搜且可能错误宣称完整结果；搜索失效只阻止末端高亮，仍提交旧导航；清洗前后的内联隐藏样式导致搜索坐标错位；同书失败任务会撤销旧可见章共享图片；finalize 回执后旧导航仍可继续。新增失败不是原六条断言被删改的结果。

正式统计为 **65 条：43 PASS / 15 FAIL / 7 OBSERVATION**。15 条失败归为下文五类问题，不等于15个独立缺陷。完整机器结果、逐项状态和文件身份见 [audit-summary.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-summary.json>)。本报告不替代主代理真实 Edge、原生宿主和全矩阵验收。

**身份、写域及证据边界。**

- 冻结 root 为 [gpt-review-r4](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4>)，被测文件只读自其中 [candidate](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate>)。扩展提交 `04f6ccb80546d6d04fe3c21f4a08775d83bfc0c6`，候选包 SHA-256 `b128b9dfbe9d7a1085cc3505f9b3edc645847cfa24da33958eb26edd685f184b`；11个源码/包/runtime文件一致性承接主代理已证实身份，本专项另记被执行四个JS的实际散列。
- 完整读取 [epub-book.js](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js>)、[search.js](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js>)、[paginator.js](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/paginator.js>)、[reader.js](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js>)全文；读取 [R4任务书](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/GPT验收结论-R3暨DeepSeek返工任务书-R4.md>)、[实施报告快照](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-report/DeepSeek实施回报-R4.md>)、提交的模型脚本及全部模型结果、相关浏览器时序脚本/结果、构建清单、边界说明和smoke/extra输出。宿主日志、兼容截图由主代理负责，本专项不宣称已亲自执行其场景。
- 仅在 [model-audit](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit>)写审计脚本/JSON/报告。未改产品、候选、旧冻结结果、用户数据；未安装依赖、运行浏览器或桌面、部署或发布。使用现有 Node `v24.18.0`、jsdom `29.1.1`、候选自带 JSZip。
- 所有业务文件全文原样加载，没有修改业务副本后当产品测试。资源URL使用create/revoke账本；真实ZIP及DOMParser/Range参与执行。旧reader回归使用公开的分页/持久化替身；新增阶段脚本执行原Paginator全文、原setSection/layout/定位/高亮，仅把jsdom不实现的iframe srcdoc/load接到document.write/load事件、设置确定几何和图片complete，并对原方法加委托观察器。持久化客户端是无磁盘替身；host.finalize由协议事件注入，不是实际窗口关闭。
- [frozen-before.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/frozen-before.json>)与 [frozen-after.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/frozen-after.json>)核验 **95个冻结输入全一致**，包括本轮candidate/提交报告/提交证据/宿主快照、任务书、身份记录及R3 model-audit原文件。它不是用户全磁盘监控，也不包含主代理正在生成的新证据。

**脚本与复跑。**

| 入口（均只写当前审计目录） | 正式结果 | 计数 |
|---|---|---|
| [audit-models.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-models.cjs>)，R3断言原样保留 | [model-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/model-results.json>) | 21 PASS / 1 OBSERVATION |
| [audit-reader-flow.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-reader-flow.cjs>)，R3断言原样保留 | [reader-flow-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/reader-flow-results.json>) | 8 PASS |
| [audit-boundaries.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-boundaries.cjs>)，R3四项边界原样保留 | [boundary-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/boundary-results.json>) | 2 PASS / 2 OBSERVATION |
| [audit-r4-boundaries.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-r4-boundaries.cjs>) | [r4-boundary-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json>) | 2 PASS / 6 FAIL / 3 OBSERVATION |
| [audit-r4-reader-stages.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-r4-reader-stages.cjs>) | [r4-reader-stage-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json>) | 7 PASS / 9 FAIL |
| [audit-r4-anchor-cancel.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-r4-anchor-cancel.cjs>) | [r4-anchor-cancel-results.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-anchor-cancel-results.json>) | 3 PASS / 1 OBSERVATION |

```powershell
# 工作目录：F:\DSH\LocalMediaPlayer-Workspace\docs\epub-optimization\gpt-review-r4\model-audit
node .\audit-models.cjs
node .\audit-reader-flow.cjs
node .\audit-boundaries.cjs
node .\audit-r4-boundaries.cjs
node .\audit-r4-reader-stages.cjs
node .\audit-r4-anchor-cancel.cjs
node .\verify-deliverables.cjs
```

公共依赖与输出限制见 [audit-common.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/audit-common.cjs>)。初始复制及冻结记录由 [prepare-audit.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/prepare-audit.cjs>)生成；保留现有基准时不要重跑这个准备脚本，只跑上表入口及 [verify-deliverables.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/verify-deliverables.cjs>)。完整交付散列见 [deliverables-sha256.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/deliverables-sha256.json>)。

新增含FAIL的脚本主动设置 `process.exitCode=2`；本执行器显示exit1，均已核查为完整结果而非未处理异常。第一次超长查询使审计参考RegExp也触及引擎限制、第一次原iframe回滚桥未逐iframe设置图片complete导致超时，分别保留 [首次边界探针错误](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-first-harness-error.json>)与 [首次阶段探针错误](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-first-harness-error.json>)；纠正审计oracle/资源替身后正式六组JSON均无harnessError。路径及超长查询从初步候选失败细分为观察，原 [分类前结果](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-preclassification.json>)亦保留，不计入65条正式统计。

**D01 · R4-07 / P2：所谓overlap没有重叠，跨块漏搜且完整性标记错误。**

证据为 [X01](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json#L21>)、[X02](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json#L121>)、[X04/X05](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json#L385>)。查询NEEDLE长度6，overlap=280，真实块界为65816；跨65536本身的样本反而正常，不能只测名义64KiB边界。

| 输入/断言 | 原实现结果 |
|---|---|
| needle起点65533、65810、65816 | 各1次，正常对照 |
| 起点65811、65815、131630，跨真实块界 | 各0次；truncated=false |
| 真实ZIP正文 `<p>` + `x.repeat(65812)` + `NEEDLE</p>` | model offset65813，扫描1/1章，total0/truncated=false |
| 999次普通命中 + 1次跨界命中 + 1次后续命中 | 本应1001，实际1000，retained1000，truncated=false |
| `😀Z`跨块、`left`与`right`间2000空格跨块 | 本应1，均0 |
| 131611个a搜索aaa，非重叠语义 | 整串43870，分块43869 |

根因：[search.js:99](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js#L99>)计算overlap，[110–114](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js#L110>)切 `chunkStart..chunkStart+SCAN_CHUNK+overlap`，但 [133](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js#L133>)直接 `chunkStart=chunkEnd`。下一块没有任何前块尾部，因此词语/代理对/空白表达式可被截断；重复匹配的非重叠位置也重新从块首起算。计数误差又传入 [141](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/search.js#L141>) 的完整性判断。

应让扫描分块保持整串匹配语义，并明确正则空白跨度不受query.length倍数保证；同时维持有限工作量让出与去重。X03确认块内 `ababa aba`→2处非重叠，以及İ/ß/emoji前缀后的NEEDLE offset3/17、Σ/σ/ς三个原UTF-16坐标正确。实现使用原字符串RegExp `/gi`，本例没有先全字符串lowercase导致长度膨胀的偏移错误；这不宣称完整Unicode多字符case folding支持。

**D02 · R4-07 / P1/P2：input、关闭搜索、换书意图没有阻止旧导航提交/布局/定位。**

证据：[prepare阶段](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json#L21>)与 [resources阶段](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json#L784>)，共六条失败。原UI打开第0章SAFE_START，搜索第1章OLDQUERY并点击，在原readText或原setSection的资源等待处挂起，然后分别执行input NEWQUERY、搜索关闭、或file change但新文件arrayBuffer尚未完成。

- prepare阶段失效后释放：原setSection仍发生DOM提交 `OLDQUERY targetTAIL`，随后layout及goToPage(0)，state.spineIndex从0变1。
- DOM已经提交、资源等待阶段失效后释放：仍执行layout及goToPage(0)，并把state.spineIndex提交为1。已发生的DOM提交不算“失效以后新提交”，这里的失败是失效以后继续布局/定位/状态提交。
- input/search-close最终highlights均空，R3 F05原断言因而PASS；这只证明末端高亮guard有效。随后普通pageNext仍能翻到第1页的四条正例单列NP PASS，不把“还能翻页”当成撤销旧导航成功。
- 完整新书已加载再释放旧任务，两阶段均PASS：无旧DOM/layout/高亮副作用，FRESH正文保留。不能把选择新文件的openIntent阶段与完整换书后的navGeneration阶段混为一谈。

[reader.js:677–684](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L677>)只更新searchGen；[969–983](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L969>)在prepare/setSection两侧只检查navGeneration；[984–993](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L984>)已经提交章节并释放资源，直到 [1000](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L1000>)才复核searchContext。原 [paginator.js:236](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/paginator.js#L236>)提交srcdoc，[249–260](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/paginator.js#L249>)只认识自己的sectionToken/destroyed，没有查询身份。新文件意图在 [reader.js:1228](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L1228>)改变openIntent，navGeneration则要到字节完成后的开书流程才更新。

这里有原业务调用链和实际jsdom文档证据；跨栏几何为替身，具体Edge可见页由主代理补证。要求仍是任务书已写明的提交前guard，不是仅把最终highlights清空。

**D03 · R4-07 / P2：搜索模型与清洗后显示模型不一致，原offset高亮错误文字。**

证据：[X06](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json#L418>)，真实ZIP→原解析→原搜索→原Paginator Range。

```html
<p>START <span style="display:none">HIDDEN</span> TARGET</p>
<svg><text style="visibility:hidden">HIDDEN</text><text>TARGET</text></svg>
```

第一例搜索模型 `\nSTART  TARGET\n`，显示模型 `\nSTART HIDDEN TARGET\n`；TARGET的offset8被原Range还原为 `IDDEN `。SVG例搜索模型TARGET、显示模型HIDDENTARGET；offset0原Range为HIDDEN。`<p>A<br style="display:none">B</p>`则搜索AB但显示模型A换行B。

原因是 [epub-book.js:141–142](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L141>)在搜索模型排除inline隐藏样式；[656](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L656>)清洗显示HTML时删除全部style，使节点重新进入显示模型；[748–751](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L748>)的plainText流程没采用同一清洗结果。两个模块调用同一个buildTextModel函数，并不保证其输入DOM一致。需要先明确保留隐藏语义还是统一去样式，再使搜索与显示采用同一有效文本。

**D04 · R4-06 / P2：prepare后段失败撤销仍被旧可见章使用的共享图片。**

证据：[X08](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-boundary-results.json#L466>)模块最小例，以及 [N-SHARED-FAIL](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json#L1808>)原reader nextChapter链。

旧章owner1已显示并引用same.png；下一章owner2复用同路径URL后，在sectionTitle依赖注入异常。prepare自己的新catch调用releaseTask(owner2)。结果旧正文OLD_VISIBLE、spine0、ready=true保留，但旧URL已进入revoke账本，resources=[]，taskOwners仍[1]。这是明确公开的依赖失败注入，不宣称普通书籍自然每次会触发title异常。

[epub-book.js:630–632](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L630>)在准备尚未成功时把_currentTaskId设为2；[595](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L595>)复用URL并记owner2；[727](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L727>)等待title；[732–735](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L732>)异常清理。关键是 [581–590](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/epub-book.js#L581>)先删owner2，再仅查询_currentTaskId=2的保留集，忽略还活着且实际显示的owner1引用。

必须保留本次早read失败40次0任务集合修复，同时让失败候选任务的释放保护仍在显示的原章引用。URL账本不证明已解码像素立即消失；主代理Edge可用fetch当前blob验证有效引用。

**D05 · R4-03/07 / 客户端契约失败：host.finalized成功回执后旧导航仍可提交和高亮。**

证据：[N-prepare-host-finalize](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/r4-reader-stage-results.json#L610>)和同结果文件的N-resources-host-finalize。点击结果导航挂起，注入原host.finalize事件，先收到 `{ok:true,revision:0}`。释放挂起后，state.finalizing=true仍执行原DOM提交/布局/goToPage/highlightRanges，最终OLDQUERY高亮及activeSearchResult恢复。

[reader.js:1395–1402](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L1395>)设置finalizing和cancel搜索引擎，但不使正在进行的nav/query失效；[1042–1047](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/candidate/reader.js#L1042>)也不校验finalizing。新的输入入口禁止操作，已经开始的导航却仍续跑。这证明客户端未形成稳定收尾边界；**不是已经证明真实WinUI窗口关闭后仍有可见高亮，也不是本例已经证实磁盘位置丢失**。持久化与窗口销毁影响交主代理关闭专项整合。

**已通过项目及边界保持。**

| 要求 | 独立结果 | 能据此确认的范围 |
|---|---|---|
| 旧书owner2迟到，新书也owner2 | F06 PASS，新书URL未revoke，资源/任务各1 | 跨书原实例释放修复有效；不同于D04同书失败候选 |
| prepare早read失败40次 | R06 PASS，taskSets/resources/pending均0 | 空任务元数据泄漏修复；不是40张图泄漏 |
| 同书A/B迟到、同path并发、共享图两种交错、destroy读中完成 | R01–R04 PASS | pending去重；旧资源迟到不登记；共享图成功路径保留 |
| 200次含图切章 | R05 PASS，每次采样资源1/任务1/pending0/create−revoke1 | 模块压力；不替代原生/浏览器进程趋势 |
| 同一jsdom环境10次换书 | B04 PASS，旧书zip/rawBytes清空，liveURL恒1 | Node内存含GC波动；未证明总进程内存严格有界 |
| 1000/1001普通样本、首/中超缓存章后续唯一词 | S01/S02/S08 PASS | 包括真实ZIP/DOM超限章，搜索不分配图片URL；D01跨块例仍失败 |
| 热取消、冷取消立即重查、零/稀疏/密集命中 | S03–S06、A03 PASS | 新固定块让出后计时器能cancel，立即新查询命中1 |
| 缺章读错误 | S07 PASS | readErrors1/truncated=true，继续后章；扫描章数表示尝试数 |
| 重复词、inline、emoji、NBSP、跨块级虚拟换行 | T01 PASS | offset/length真实Range；Range不含虚拟换行属正常 |
| hidden span/br、closed/open details、SVG defs/desc/display/visibility/symbol | T02/T03 PASS | hidden属性及SVG presentation属性本例排除，aria-hidden视觉文本保留；D03内联style输入另失败 |
| 路径一次解码、%20/%2520、SVG片段 | P01 PASS | 两真实条目完整字节分别[1,2,3]/[9,8,7]；href/xlink与#one/#two保留；不证明像素解码 |
| HTML/SVG内层与reader外层协议 | P02/F08 PASS | javascript/data/file/vbscript/mailto/about/blob/畸形https不转外开；http/https允许 |
| 精确IDPF、真实ZIP目标、MIME | E01/E02/X09 PASS | 缺条目、伪字体MIME、未知/Adobe、混合XHTML拒绝；编码字体路径及标准MIME正例为声明分类，不是字体解混淆/真实解码 |
| 图片null/缺失/string0与数字0锚点 | B03 PASS | null/缺失/string0走元素，数字0保留文字语义，坏元素退fraction |
| 纯元素重排与late timer | A01/A02 PASS | 原requestLayout宽600→900，page3→2保持elementPath[1]/textOffset:null；晚资源定时器采用confirmed anchor；几何替身 |
| 真after-DOM新书失败回滚 | N-ROLLBACK PASS | 原setSection已提交新文档并layout后抛错，旧正文/offset11/pageIndex2/旧图URL恢复，新图被revoke |

**OBSERVATION与未覆盖必须分开。**

1. B01：8,388,608上限单位是保留的UTF-16文本字符数，中文按通常两字节表示约16MiB payload；并非总内存8MiB。rawBytes/ZIP、临时整章DOM、结果摘要、显示DOM和URL均在其外。
2. B02/A04：2Mi UTF-16冷解析的Node/jsdom样本约82–86ms，解析期间timer未运行，约82.9ms进入cancel。取消在整章同步DOM/model完成后的yield边界生效，尚无内部可取消点；不把单次Node耗时外推成Edge严重卡顿。热扫描新调度保证已经独立通过。
3. X11：74,001 UTF-16的字面查询直接触及原生RegExp `Regular expression too large`。长于chunk查询已执行，但受引擎长度边界限制，不能据此单独归因chunk，也没有把任意超长查询支持写成PASS。UI如何呈现该错误本专项未覆盖。
4. E03/X10：`font.otf`及根解释失败的`../OPS/f.otf`可以被OPF目录兜底重新指向存在字体；编码斜杠被拒。作为路径解释策略观察，未宣称文件系统越界/DRM绕过。R4-08主要缺目标/伪MIME修复已过；是否允许这种非根兜底需明确契约。
5. X07：title/defs/symbol模板排除，aria-hidden视觉文本保留；use已明确声明不支持，本次实际fixture也未把模板文字纳入模型。另 `g visibility=hidden`下子text显式visibility=visible也被整棵裁掉，属于应由Edge核查的不同SVG继承边界，不以“use不支持”概括它。
6. **未覆盖**：真实Edge/WinUI渲染与进程趋势、字体可解码/真正IDPF字节解混淆、SVG use展开及完整继承绘制、真实晚图片/字体跨ready的像素恢复、完整CSS可见性、原生关闭销毁/I/O结果。主代理另做真实Edge，本报告没有浏览器或桌面调用。

**实施方22+8项证据核对。**

[assertion-comparison.json](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/model-audit/assertion-comparison.json>)证明提交 [audit-models.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/model/audit-models.cjs>)及 [audit-reader-flow.cjs](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/model/audit-reader-flow.cjs>)分别与R3原文件逐字节相同：**没有删断言或改变这两份脚本的断言。** helper只适配源码/输出路径，遗留R3提交/包SHA常量；提交结果四个实际JS散列与本冻结候选一致，因此应纠正身份头，不能仅凭旧常量否定实际执行的R4代码。

[R4报告:158](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-report/DeepSeek实施回报-R4.md#L158>)的22项0FAIL/1OBS与8/8在有限断言范围内成立；没有包含R3另外四项boundary回归，本次已补。其 [132](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-report/DeepSeek实施回报-R4.md#L132>)把T03说成use场景观察、[140](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-report/DeepSeek实施回报-R4.md#L140>)把E03写成use/URI，均与实际脚本不符：T03没有use，且PASS；E03只测CipherReference根URI兜底。use策略声明是边界说明，不等于该断言已运行。

提交浏览器 [C05](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/image-ack/results.json#L210>)同时记录highlights=[]和spine=1，脚本 [25](<F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/gpt-review-r4/submitted-evidence/tools/review-image-ack.cjs#L25>)只以旧高亮判断defect，因此没有证明导航没有落地。这里不是伪造绿灯，而是断言只验高亮、无法关闭任务书要求的全过程guard。

最终总验收应保留已证实修复，并将D01–D05及明确未覆盖项纳入后续返工/复验。所有增量发现已发送主代理；未直接联系实施方。全部后台任务已收集，正式结果与冻结哈希核验齐备。
