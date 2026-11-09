# DeepSeek 实施回报 — R3

> 对应 GPT 任务书：`docs/epub-optimization/GPT验收结论-R2暨DeepSeek返工任务书-R3.md`  
> 任务书 SHA-256：`212B47E2B8B977F7EEC77E55FFDE90CC6BE64374A538969E79AD28203A46C323`  
> 实施日期：2026-10-28  
> 结论：**R3 十二组代码修改已完成并构建候选；本轮新增的浏览器/模块/宿主专项检查全部通过。R2 冻结产物未被覆盖。** 仍有几项原生 WebView2/双窗口/真实书库场景只能做间接证据，下面逐项列出，不把它们写成已通过。

---

## 1. 冻结边界与 R3 身份

R2 冻结产物保留不动：

| 项 | R2 冻结值 |
|---|---|
| 扩展 commit | `c96c912ea1ab1e9a7a311620663ea4f6634a3d18` |
| 候选包 | `docs/epub-optimization/candidates-r2/lyra-ext-epub-0.6.1-candidate.zip` |
| 候选包 SHA-256 | `65CF0784AD4A006BA2444D1A5B08EF2C354B116E71325DFCC5E9A2C194170063` |
| 宿主 DLL SHA-256 | `21932E2C246BF6152BA9C42834F60654A7A5AE239151B645F0EE8F14F17A30DA` |
| 宿主 exe SHA-256 | `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335` |
| 证据目录 | `docs/epub-optimization/r2-evidence/` |

本轮 R3 独立产物：

| 项 | R3 值 |
|---|---|
| 扩展分支 | `D:\Temp\Lyra-Extension-Epub`，branch `r3` |
| 扩展 commit | `ba6f763f9579a808ddc201b82b5867412265c104` |
| 扩展版本 | `0.7.0`（**candidate，不发布**） |
| R3 候选包 | `docs/epub-optimization/candidates-r3/lyra-ext-epub-0.7.0-candidate.zip` |
| 候选包 SHA-256 | `847D8047702E8DE6A8DEC8B7AE9E3FB60693686400B71AA65C2B134A7270636C` |
| R3 宿主 DLL SHA-256 | `8067990EBBAF126239B1F3B8CC936D36CB3B1F47DC317D541FDF4CE9C4A9905E` |
| R3 宿主 exe SHA-256 | `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`（exe 未变，DLL 变化） |
| R3 隔离运行目录 | `D:\Temp\lyra_epub_r3_candidate` |
| R3 宿主状态根 | `D:\Temp\lyra_epub_r3_candidate\data\reader\epub` |
| R3 证据目录 | `docs/epub-optimization/r3-evidence/` |

构建命令仍为：

```powershell
dotnet build F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_epub_host_r3
```

未覆盖用户安装目录、未覆盖 R2 包、未覆盖用户书库或用户存档；R3 运行目录的 `backend` 通过 junction 复用只读运行时，`data` 是新建的隔离测试数据。

---

## 2. 十二组返工实现

### R3-01 偏好字段事务：修改时版本、不可回退、分开 ack/超时

实现位置：

- `app/WinUIGui/ReaderStateStore.cs`
  - `FieldGrants`：`bookId|fieldVersion -> sessionId`。
  - `_globalFieldSequence` + `ReadMaxFieldSequenceLocked()`：宿主授予的字段版本单调递增，重启后从磁盘 `fieldSequences` 续接。
  - `GrantInteraction` 返回 `{ interactionToken, fieldVersion }`；每个用户交互在**宿主收到请求时**授予字段版本。
  - `SavePreferences(sessionId, bookId, preferences, fieldVersions)`：只接受当前会话拥有的字段版本，且版本必须大于已存版本；返回 `applied`/`rejected`。没有任何 applied 时宿主返回 `PREFERENCE_STALE`，不伪装成功。
- `app/WinUIGui/ExtensionHostWindow.xaml.cs`
  - `state.interact` 透传 `fieldVersion`；`state.savePreferences` 读取 `bookId` 与 `fieldVersions`。
- `reader-state.js`
  - `nextPreferenceVersion(bookId)`：字段版本请求串行，并在**调用时**捕获 host epoch（不是 flush 时）。
  - `savePreferences(patch, fieldVersions, bookId)`：把字段版本和 bookId 一起提交。
- `reader.js`
  - `pendingPrefs` 每字段保存 `{value, changeId, versionPromise}`。
  - `applyPreferences` 在用户 input/change 时入 pending；`flushPrefs` 对每字段先取版本，再只提交快照中仍未被同字段新修改替换的值。
  - 旧回执只清除 `changeId` 相同的字段；在途期间的新修改会重新排一次 flush，不会被旧 ack 清掉。
  - 保存位置采用“显式关窗前一次性刷盘”，不在 debounce 时发 commit；`flushSave` 捕获 token/位置快照，失败时保留 `failedSaves`，不把失败显示成成功。

新增证据：

- 浏览器 `R3-PREF-INFLIGHT`：28→32 快速输入，最终 localStorage `fontSize=32`。
- 浏览器 `R3-PREF-INFLIGHT-ACK`：第一笔 savePreferences 被人为延迟在途时再改 32，旧 ack 到达后仍需第二次提交，最终磁盘为 32。
- 浏览器 `R3-PREF-READ-FAIL`：读偏好故障时旧 `fontSize=32/theme=sepia` 不被清空。
- 宿主审计 `H-R3-PREF-VERSION`：18（v1）→32（v2）后迟到的 v1 被拒，伪造 `long.MaxValue` 被拒，最终 `fontSize=32`。

### R3-02 激活/导航/会话代次：旧 hello、旧 interact 不能复活

实现位置：

- `ExtensionHostWindow.xaml.cs`
  - `_activationEpoch`：窗口 Activated 自增；`PostHostState` 推送当前 epoch。
  - `_navEpoch`：每次 `NavigateToExtension` 自增；hello 返回两个 epoch。
  - `IsCurrentSource` 校验 URL 的 `sessionId`；hello 不再接受任意 session。
  - `state.interact` 校验请求的 `activationEpoch`/`navEpoch`，不匹配返回 `STALE_OPERATION_EPOCH`。
  - `NavigationStarting`：自己的导航被抑制；外部 Reload/NewDocument 轮换 session 后重新导航；旧页面迟到的消息因 session/source 不匹配被拒。
- `reader-state.js` / `reader.js`
  - hello 保存 `_hostEpoch`；`markInteraction` 与 `nextPreferenceVersion` 在调用时冻结 epoch；`host.state` 更新 epoch。

新增证据：

- 宿主审计 `H-R3-OLD-INTERACT`：旧 activation epoch 的 interact 返回 `STALE_OPERATION_EPOCH`，当前 epoch 成功。
- 宿主审计 `H-R3-REFRESH-HELLO`：Reload 后 session 轮换；旧 session hello 无回复；新 session hello 成功。
- 真实候选日志：见 `r3-evidence/host-finalize-r3-final.log`，`state.interact` 带 `activationEpoch:1, navEpoch:1`，宿主 `ok=True`。

### R3-03 关闭 finalize：ack 与 timeout 分开记录、独立关闭截止时间

实现位置：

- `ExtensionHostWindow.xaml.cs`
  - `_finalizeRequestId`、`_finalizePending`、`FinishFinalizeAndClose(reason, requestId, revision)`。
  - `CLOSING` 记录 requestId；`CLOSE_ACK/CLOSE_TIMEOUT/CLOSE_ERROR` 分开记录；ack 必须 exact requestId，且 session 匹配。
  - 空/错误 requestId 被忽略，不能提前关闭。
  - 关闭定时器独立于普通导航/加载定时器；`StopTimers` 跳过 `_closeTimer`，导航失败不能取消关闭期限。
- `reader.js`
  - `finishAndAck(requestId)` 停止翻页计时、搜索、在途 turn，settle paginator，flush preferences/position，再发 `host.finalized`。
  - `bindHostMessages` 处理 `host.finalize`；`beforeunload/pagehide` 做最后 flush。

新增证据：

- 真实 WebView2 候选日志（最终扩展 0.7.0）：
  - `CLOSING request=e8efc43db8924229baca4d1086230ecd`
  - 扩展回 `host.finalized` 同 requestId、同 session、`ok:true`
  - 宿主记录 `CLOSE_ACK request=e8efc43db8924229baca4d1086230ecd revision=0`
  - 没有 timeout 行。
  - 上一轮长书实际关闭同样为 `CLOSE_ACK ... revision=3`，说明位置/设置刷盘计入 revision。
- 宿主审计 `H-R3-FINALIZE`：
  - empty requestId 不关闭；
  - exact ack 关闭；
  - 无 ack 由 timeout 关闭；
  - 关闭期间注入导航失败后 timeout 仍然关闭。

### R3-04 本地状态读失败、坏状态不阻断正文、失败快照保留

实现位置：

- `reader-state.js`
  - 本地 position/bookmarks JSON 损坏时 `load` 返回安全空状态并附 `warnings`，不清空、不覆盖原坏文件；之后写操作返回 `LOCAL_CORRUPT`，避免把坏文件覆盖成“正常空状态”。
  - 偏好读取失败设置独立标志；保存时抛 `LOCAL_READ_FAILED`，旧偏好字段不被清空。
  - 单条书签限制与宿主对齐：摘要 ≤256 字符、单条序列化 ≤4 KiB、id ≤120。
- `ReaderStateStore.cs`
  - 坏文件备份失败时返回 `Unreadable`，不再把坏文件当空状态覆盖。
- `reader.js`
  - `failedSaves` 保存失败位置快照（含冻结 token）；换书后仍保留；重开同一本书或恢复存储后尝试用原 token 重试，过期明确显示失败，不申请新资格覆盖别人。
  - 打开新书成功时不丢弃旧书的 failedSaves。

新增证据：

- 浏览器 `R3-LOCAL-CORRUPT`：position/bookmarks 坏 JSON 后仍 ready，能显示正文。
- 浏览器 `R3-FAILED-SNAPSHOT`：保存时注入 state 写入失败 → 换书 → 恢复写入 → 重开原书，failedSaves 清空且磁盘位置为失败前页。
- 浏览器 `R3-PREF-READ-FAIL`：读偏好故障后旧字段保留。
- 浏览器 `R3-BOOKMARK-DELAY`：257 中文字符摘要被本地客户端拒绝（`BOOKMARK_TOO_LARGE`）。
- 宿主读失败/备份失败的真实文件级路径仍是间接验证（见第 5 节残余项）。

### R3-05 搜索取消、防抖、旧结果与跨书隔离

实现位置：

- `search.js` 重写：
  - 索引生成与查询使用 generation；`cancel()` 递增 generation、丢弃旧 build promise、清空 cache 引用。
  - 冷索引构建期间取消后再发新查询，旧 build 不会复用；新查询重新建。
  - 长章节扫描每 128 个匹配/每 6 章 yield，取消可在扫描中途生效。
  - 索引缓存上限 8 MiB 字符，但**继续扫描后续章节**；超限只置 `truncated`，不停止全书。
  - 结果保留上限 1000，`total` 仍统计全部匹配；`truncated` 表示结果或缓存截断。
  - 返回 `scannedSections/totalSections/cacheTruncated/readErrors` 供 UI 明确告知范围。
- `reader.js`
  - 输入变化立即 `invalidateSearch`（清 timer、清旧结果、取消 engine），再等 300ms 执行；关闭搜索清 timer 和结果。
  - 每个结果按钮绑定产生时的 bookId/openIntent/queryGen/query；跳转前校验四者，过期结果不可点击套到新书。
  - 换书时清空旧结果与 engine。
  - 隐藏/关闭内容进入统一可见正文模型（`epub-book.buildTextModel`）：`hidden`、`aria-hidden=true`、关闭 `<details>` 的非 summary、SVG `defs/desc` 不进入搜索/分页文字模型。
- 搜索结果显示 `找到 total+ 处`、扫描章节数、截断原因；零结果但 truncated 时明确提示“不能据此判定全书无匹配”。

新增证据：

- 浏览器 `R3-SEARCH-RANGE`：跨两章 1001 个 needle → 1000 个按钮 + `找到 1001+ 处` + 截断提示。
- 浏览器 `R3-SEARCH-VISIBLE`：`HIDDEN_SECRET`/`CLOSED_SECRET`/`SVG_DEF_SECRET` 0 结果，可见 `AB` 命中。
- 浏览器 `R3-SEARCH-CLOSE-DEBOUNCE`：输入后立刻关闭，700ms 后无旧结果。
- 浏览器 `R3-SEARCH-CROSSBOOK`：旧书搜索后开新书，旧按钮清空。
- 模块 `R3-M05`：热索引取消被观察。
- 模块 `R3-M06`：冷索引取消后立即新查询成功。
- 模块 `R3-M07`：单章 8 MiB 级扫描中途可取消。
- 模块 `R3-M12`：隐藏/关闭/details/SVG defs 模型断言通过。
- GPT R2 模型审计 `M03/M04` 对 R3 源码重跑通过（1001 跨章、8 MiB 首章后仍找到后续章）。

### R3-06 书签事务与限额

实现位置：

- `reader.js`
  - add/delete 在开始时捕获 bookId/openIntent；回执只在原书/原打开意图仍当前时修改 UI；迟到回执不进新书。
  - 本地重复位置仍由 `_key` 去重；宿主/本地都用 id 幂等。
- `reader-state.js`
  - 本地 add/delete 在坏 bookmarks 状态上拒绝覆盖；单条限额与宿主对齐。
- 宿主已有单条摘要 256 字符、单条字节上限、每本 500 条、id 校验保持；R3 不再放宽。

新增证据：

- 浏览器 `R3-BOOKMARK-DELAY`：延迟 add 期间换书，旧回执不进入新书；257 字符摘要本地被拒。
- 浏览器 smoke `A18a`、extra `A18b` 保留通过。

### R3-07 图片/纯元素页锚点：`textOffset:null` 不再变第一页

实现位置：

- `paginator.js locateAnchor` 分支重排：
  - 只有 `typeof textOffset === "number" && Number.isFinite(...)` 才走文字分支；`null` 不再 `Number(null)=0`。
  - 有 `elementPath` 且无有效文字 offset 时，优先按元素矩形/元素页定位，再做文字 quote 回退。
  - `chapterFraction` 只作为最后回退。
- `captureAnchor` 对纯图页保留 `elementPath`、`textOffset:null`、`chapterFraction`。

新增证据：

- 浏览器 `R3-IMAGE-ANCHOR`：混合章节 16 页，纯图末页 anchor 为 `textOffset:null, elementPath:[60,0]`；翻回再翻到末页后保存、刷新，恢复页=原页且图片元素页=原页（不是第 1 页）。
- 旧 extra `A10c` 仍通过（图片页 anchor 有 elementPath）。
- 尚未做真实 WebView2 图片页关窗/重开证据（见第 5 节）。

### R3-08 资源任务所有权与迟到资源

实现位置：

- `epub-book.js`
  - 每次 `prepareSection` 分配 `taskId`，并把它设为 `_currentTaskId`。
  - `_resourceBaseUrl(path, taskId)`：资源读取挂起期间若任务已旧，返回空、revoke 且不登记；已有资源登记到当前任务路径集合。
  - `_taskResources`：按任务记录路径；`releaseTask(taskId)` 只撤销该任务独占且当前任务不再共享的资源。
  - `releaseResourcesExcept` 保持清掉当前章以外的旧 blob。
- `reader.js`
  - `navigateToSpine` 失败/过期时 `releaseTask`；提交成功后释放 previous owner 的旧任务资源；换书销毁旧 book。

新增证据：

- 模块 `R3-M10`：同书 A/B 旧章读挂起 → 当前章 C 提交 → A/B 迟到完成，最终 `_resources` 只包含 C 的 `OPS/current.png`，pending 为 0。
- 模块 `M01/M02`（GPT R2 审计重跑）：destroy pending 不登记、同路径并发共享一个 URL 通过。
- R3 浏览器长测试没有出现旧章图片串图。

### R3-09 正文提交中途失败回滚到“实际旧文档”

实现位置：

- `reader.js openBookBuffer`：
  - 打开前捕获旧 spine/page/anchor/prefs/bookmarks。
  - 失败时销毁新 book，不只恢复全局字段；用旧 spine + 旧 anchor 重新 `navigateToSpine`，恢复实际 iframe 文档。
  - 恢复失败时明确显示可重试错误，不伪装成功。
  - 旧书资源/UI 保留；只有全失败才清空。

新增证据：

- 浏览器 `R3-ROLLBACK-DOC`：新书 `prepareSection` 注入失败后，屏上正文仍是旧书 `N paragraph 0...`，不是新书内容；status 显示已保留原书。
- 覆盖的是 prepare/提交阶段失败；layout/分页中途失败和旧版本加载失败组合仍是残余风险。

### R3-10 外层外部协议白名单

实现位置：

- `reader.js handleLink` 自身再做一层 `safeExternalHref`：只允许 `http:`/`https:`，不再信任清洗层；`javascript:`、`data:`、`file:` 等直接阻止。
- 内层 `epub-book` 已有清洗；两层独立，SVG/伪造 `data-lyra-external` 不因内层漏检而打开。

新增证据：

- 浏览器 `R3-EXTERNAL-WHITELIST`：`javascript:`、`data:` 两次 `onLink` 后 `window.open` 调用数为 0；`https://example.com/ok` 正常打开。
- 旧 R2 链接测试证据保留；R3 新增外层独立断言。

### R3-11 字体混淆精确分类

实现位置：

- `epub-book.js _validateEncryption` 移到 manifest 解析之后：
  - 只接受 Algorithm 精确等于 `http://www.idpf.org/2008/embedding`。
  - `CipherReference URI` 必须解析到 manifest 条目，且 media type 是字体类（font/otf/ttf/opentype/woff 等）；指向 XHTML 等非字体资源明确拒绝。
  - 一个 encryption.xml 里出现非 IDPF 算法（含 Adobe AES）即拒绝，不再因字符串含 font/idpf 就放行。
  - 只有全部受保护目标都是受支持的字体资源，才设置 `fontObfuscated=true` 用系统字体继续阅读。

新增证据：

- 模块 `R3-M14`：IDPF + font/otf 可打开；未知算法拒绝；IDPF 指向 XHTML 拒绝。
- 真实合成书打开/阅读无影响（字体统一系统字体）。

### R3-12 其余宿主边界：序号、旧操作、关闭定时器

实现位置：

- `ReaderStateStore.cs`
  - 位置保存同一序列号用 `JsonNode.DeepEquals` 做语义幂等，不再比较不同编码字符串；中文/转义等价位置可幂等。
  - 新 `interactionToken` 授予时重置本资格 `positionSequence` 空间；跨资格先后由 token/epoch 裁决，不因系统时钟回拨误拒。
  - 旧 token、旧 owner、旧 seq 拒绝覆盖新位置。
- `ExtensionHostWindow.xaml.cs`
  - 关闭期间导航失败不取消关闭截止时间。
  - 旧 hello / 旧 session / 旧 source 不再恢复旧状态。
  - 最终 ack 必须 exact requestId，空/错 requestId 忽略。
- `reader-state.js`
  - `nextSequence` 严格递增，不依赖墙上时钟。

新增证据：

- 宿主审计 `H-R3-IDEMPOTENT-SEQ`：中文等价位置重复序号返回 idempotent；同序号不同内容返回 `DUPLICATE_SEQUENCE`；新 token 的 seq=1 可保存。
- 宿主审计 `H-R3-FINALIZE` 的 nav-fail 分支。
- 宿主审计 `H-R3-REFRESH-HELLO`。

---

## 3. 协议/契约变化与兼容性影响

R3 宿主与扩展的消息契约有意变化，GPT 复核时请先看这一节：

1. `state.interact` 请求新增可选 `activationEpoch`/`navEpoch`；响应 `result` 新增 `fieldVersion`。宿主对缺失/错误 epoch 返回 `STALE_OPERATION_EPOCH`。
2. `state.savePreferences` 从 R2 的 `{sequence, preferences}` 改为 `{bookId, preferences, fieldVersions}`；宿主按 `fieldVersions` 校验字段授权。缺失 `fieldVersions` 或没有 applied 时返回错误（`PREFERENCE_STALE`/`MISSING_FIELD_VERSION`）。
3. hello 不再接受任意 `sessionId`；只接受当前 URL `sessionId` 的 hello。`hello` 响应新增 `activationEpoch`/`navEpoch`。
4. `host.finalized` 必须带精确 `requestId`（可带 sessionId/ok/revision）；空/错 requestId 只记 `CLOSE_IGNORED`，不提前关闭。
5. 位置保存的 `positionSequence` 在同一 interaction 内严格递增；新 interaction 重置该资格序号空间。语义幂等用 JSON 结构比较。
6. 兼容性：
   - **R3 扩展 + R3 宿主**：本轮主路径，实际候选已通过。
   - **R2 扩展 + R3 宿主**：旧扩展不会发 epoch/fieldVersions，`state.interact` 和偏好提交会被新宿主拒绝；旧扩展的旧 ack 也不能关闭新宿主的 finalize。这是 R3 契约升级的预期影响，不能把它当“旧客户端仍完整兼容”。
   - **R3 扩展 + R2 宿主**：R2 宿主没有 epoch/fieldVersion 契约，R3 客户端不会拿到字段版本；不承诺完整偏好事务。
   - **旧/旧**：R2 冻结组合继续可用；本轮未改动 R2 包。
   - PDF 宿主复用同一窗口代码；`IsCurrentSource` 现在要求 session 查询参数，旧 PDF 扩展 URL 若不带 session 需单独回归；R3 证据不覆盖 PDF。

---

## 4. 测试与原始证据索引

所有证据在 `docs/epub-optimization/r3-evidence/`：

| 文件 | 内容 | 结果 |
|---|---|---|
| `r3-browser-results.json` / `r3-browser-checks.out` | R3 浏览器 13 项 | 13/13 PASS |
| `r3-model-results.json` | 搜索取消/冷索引/长扫描/资源/可见模型/加密 6 项 | 6/6 PASS |
| `r3-host-audit-results.json` | R3 宿主字段版本/旧 interact/旧 hello/finalize/幂等 5 项 | 5/5 PASS |
| `host-finalize-r3-final.log` | 最终 0.7.0 扩展真实 WebView2 关闭 ack | `CLOSING` + `host.finalized` 同 requestId/session + `CLOSE_ACK`，无 timeout |
| `host-finalize-r3.log` | 上一轮真实 WebView2 长书关闭 ack | `CLOSING` + `CLOSE_ACK ... revision=3` |
| `real-host-identity.json` | 最终候选进程/程序集/扩展身份 | 见文件 |
| `real-webview2-r3-open.png` | 真实 WebView2 打开正文截图 | 视觉证据，不替代断言 |
| `r3-reader-smoke.out` | 原 12 项 smoke | 12/12 PASS |
| `r3-reader-extra.out` | 原 9 项 extra | 9/9 PASS |
| `r3-package.png` | 干净解包后 0.7.0 包 smoke | 书标题/目录/页数/ready 正常 |
| `r3-build-manifest.json` | 包/宿主/扩展/测试/SHA-256 汇总 | 见文件 |

R3 浏览器项目清单：

`R3-PREF-INFLIGHT`、`R3-PREF-INFLIGHT-ACK`、`R3-IMAGE-ANCHOR`、`R3-SEARCH-RANGE`、`R3-SEARCH-VISIBLE`、`R3-SEARCH-CLOSE-DEBOUNCE`、`R3-SEARCH-CROSSBOOK`、`R3-LOCAL-CORRUPT`、`R3-ROLLBACK-DOC`、`R3-FAILED-SNAPSHOT`、`R3-BOOKMARK-DELAY`、`R3-PREF-READ-FAIL`、`R3-EXTERNAL-WHITELIST`。

模块项目清单：

`R3-M05`（热取消）、`R3-M06`（冷取消后新查询）、`R3-M07`（长扫描取消）、`R3-M10`（旧章资源）、`R3-M12`（可见模型）、`R3-M14`（字体混淆分类）。

宿主审计项目清单：

`H-R3-PREF-VERSION`、`H-R3-OLD-INTERACT`、`H-R3-REFRESH-HELLO`、`H-R3-FINALIZE`、`H-R3-IDEMPOTENT-SEQ`。

宿主审计不是完整原生窗口测试，证据文件内已注明 `evidenceKind: original C# host source with headless event doubles; not native WinUI/WebView2`。

---

## 5. A01–A25 状态复核

| ID | 状态 | 说明 |
|---|---|---|
| A01 | PASS | smoke TOC/spine 分离 |
| A02/A02b | PASS | smoke fallback toc + extra EPUB2 NCX |
| A03 | PASS | smoke 章内片段 |
| A04/A04b | PASS | extra 身份、旧版不能抢占 URL 书 |
| A05 | **未完成** | 慢章节/加载中取消没有独立 R3 证据 |
| A06 | PASS（本地/失败路径） | smoke 坏文件可恢复；R3 rollback 注入验证 |
| A07 | PASS（本地） | smoke 文字位置恢复；A10b/A10c |
| A08 | PASS（extra） | 无 idle rAF 循环 |
| A09 | PASS（extra） | 同名/同内容身份 |
| A10/A10b/A10c | PASS | 长段文字/图片页锚点本地回归 |
| A11 | **未完成** | 资源边加载边关窗真实 WebView2 证据不足 |
| A12 | PASS | smoke 20 次快速翻页 |
| A13 | PASS（extra） | 无 idle 循环 |
| A14 | PASS | smoke 选择不翻页 |
| A15/A15-520/A15-1400 | PASS | smoke + extra 视口 |
| A16 | PASS | smoke 书 iframe sandbox |
| A17 | PASS | smoke 全文搜索；R3 1001/隐藏/取消另列 |
| A18/A18a/A18b | PASS | smoke/extra 书签；R3 迟到回执 |
| A19 | PASS（真实宿主） | 实际关闭前 `state.savePosition` 已提交，`CLOSE_ACK` revision 非零 |
| A20 | PASS（本地/模块） | 坏 position/bookmarks 不阻断；失败快照 |
| A21 | **部分** | R2 兼容矩阵未完成真实旧/旧、新/旧组合；R3 契约变化见第 3 节 |
| A22 | **沿用 R2 证据** | R2 真实双书证据保留，R3 未重跑真实双书 |
| A23 | PASS（模块） | 资源所有权/迟到 blob；真实图片页关窗未验 |
| A24 | **部分** | finalize ack 真实通过；加载中/搜索中关窗真实卡顿未完全覆盖 |
| A25 | PASS（隔离） | R3 未覆盖用户安装/用户数据/R2 包 |

---

## 6. 本轮残余风险（请 GPT 重点复核）

1. **双窗口真实 UI 偏好竞态**：字段版本和 client 在途逻辑分别有宿主审计、浏览器测试；没有用两个真实阅读窗口同时拖动设置滑块的端到端证据。
2. **真实 WebView2 图片页关窗/重开**：图片页锚点在浏览器和模块测试通过，但未在原生候选里做“纯图页关闭→重开”截图断言。
3. **加载/搜索中关闭的真实卡顿**：finalize ack 已真实覆盖；搜索/加载进行中关闭的耗时未做原生量化。
4. **A05/A11/A21/A22/A24** 如表中未完成/部分，没有用 R3 新证据替换 R2 结论。
5. **同步失败第 3 阶段回滚**：浏览器注入覆盖 prepare/提交失败；分页/布局中途失败、旧版本迁移失败组合仍有限。
6. **兼容性**：R3 契约与 R2 客户端不双向兼容；旧/旧保留，新/新为本轮范围。PDF 复用窗口的 session 校验未单独回归。

以上未通过项不写成通过；如 GPT 复核认为需要，我可以按指定场景继续补原生产/真实双窗口/真实图片书证据。

---

## 7. 变更清单（关键文件）

- `D:\Temp\Lyra-Extension-Epub\search.js`（重写）
- `D:\Temp\Lyra-Extension-Epub\reader-state.js`
- `D:\Temp\Lyra-Extension-Epub\reader.js`
- `D:\Temp\Lyra-Extension-Epub\epub-book.js`
- `D:\Temp\Lyra-Extension-Epub\paginator.js`
- `D:\Temp\Lyra-Extension-Epub\index.html` / `reader.css`（与 R2 相同的入口/样式，未改发布资源）
- `app\WinUIGui\ReaderStateStore.cs`
- `app\WinUIGui\ExtensionHostWindow.xaml.cs`
- `tests\r3-checks.cjs`、`tests\r3-model-checks.cjs`
- `docs\epub-optimization\r3-host-audit\Program.cs` + `UiStubs.cs`
- `docs\epub-optimization\candidates-r3\lyra-ext-epub-0.7.0-candidate.zip`
- `docs\epub-optimization\r3-evidence\**`

---

## 8. 结论

R3 十二组代码均已落盘并进入候选，新增 R3 证据 24 项断言全部通过（浏览器 13、模块 6、宿主审计 5），真实 0.7.0 扩展 + R3 宿主的关闭 finalize 已拿到同 requestId/session 的 `CLOSE_ACK`，不是 timeout。R2 冻结包、宿主旧 DLL 证据、用户数据均未被覆盖。

但这不是“全部原生场景已验收”：双窗口真实偏好竞态、真实 WebView2 图片页关窗、加载/搜索中关闭卡顿、A05/A11/A21/A22/A24 仍按上表保留未完成/部分状态。请 GPT 按 R3 任务书复核后决定是否接受或继续返工；需要补原生证据时我按具体场景继续做。
