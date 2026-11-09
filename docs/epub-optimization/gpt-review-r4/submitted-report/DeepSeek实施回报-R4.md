# DeepSeek 实施回报 · R4

- 任务书：`docs/epub-optimization/GPT验收结论-R3暨DeepSeek返工任务书-R4.md`，SHA-256 `20E37F00B70E7E7BD72CE072164049269CBE94998FBE147ABD50A7A59F989569`
- 分支 / 提交：`D:\Temp\Lyra-Extension-Epub`，`r4` / `04f6ccb80546d6d04fe3c21f4a08775d83bfc0c6`
- 候选包：`docs/epub-optimization/candidates-r4/lyra-ext-epub-0.8.0-candidate.zip`
  - SHA-256 `B128B9DFBE9D7A1085CC3505F9B3EDC645847CFA24DA33958EB26EDD685F184B`，93,401 bytes
  - 扩展版本 `0.8.0`；包内 `index.html` 与候选运行目录一致
- 宿主候选（隔离运行目录）：
  - `WinUIGui.dll` `9EBA643FA6E3933B5A3F86AFC1BE424FFAA08A09BF11B9AC89D2C451E3A9DF95`
  - `WinUIGui.exe` `D0F1BB58B07D0B6AEAEB463C841914D9594D0A803E3831FC272A6A5FFED3B335`
  - 运行目录 `D:\Temp\lyra_epub_r4_candidate`，数据 `D:\Temp\lyra_epub_r4_candidate\data`
- 证据目录：`docs/epub-optimization/r4-evidence/`
  - 清单：`build-manifest.json`
  - 证据文件 SHA：`SHA256SUMS`（SHA-256 `A7BC43C89CB6E30DF1F485C25BAAE97B70A41CB12B938C47C68CFBA8CA0EF48E`）
  - `SHA256SUMS` 覆盖 54 个文件；所有文件均在 `r4-evidence` 新目录，未改 R3 freeze / R3 证据 / R1/R2 冻结物。

本轮没有发布、部署、安装，也没有修改用户书库位置（全部 R4 运行在隔离 `D:\Temp\lyra_epub_r4_candidate`）。

## 1. 先修复的 R4-01 ~ R4-05

### R4-01 无损字段版本 / 不透明 grant

改动：

- `reader-state.js` 协议升到 `lyra-reader` v2；新增 `state.grantPreferences`，宿主返回 `pg-<随机>` 不透明字符串，不再把 .NET ticks 的 19 位整数交给 JS `Number`。
- `state.savePreferences` 请求体为 `{scope, preferences, grants:{field:grant}}`；先整体校验全部 grant，再事务写 `preferences` + 新的内部小整数 `preferenceOrders`。
- 旧 `fieldSequences` 保留不覆盖；旧 `preferences.json` 不丢原值。宿主 `LatestPreferenceOrders` 用于拒绝旧操作。
- 旧同 operation 重发同值返回 `applied + idempotent`，不会重复写；同 grant 改值拒绝。

证据：

- 宿主直连审计：`r4-evidence/host-audit/r4-host-results.json`，`H-R4-01-GRANT-STRING` PASS。
  - 原始 grant：`pg-63e0b19db84141b68c7e2b90712c10a5`（另有 `pg-066e828effcd4f409872b2fc03f28887`）
  - 旧数据 `fieldSequences.fontSize = 639287159852507025` 在保存 32 后仍在磁盘
  - 新写入 `preferenceOrders.fontSize = 2`，`preferences.fontSize = 32`
  - 先 18、后 32：旧 `18` 提交返回 `PREFERENCE_STALE`，`32` 返回 `applied`，同 32 重发返回 `idempotent:["fontSize"]`
- 真机 R4 最终关闭：`native-final-pending-prefs.json` 显示 `fontSize=32`、`lineHeight=2.1`，`fieldSequences` 仍为空、`preferenceOrders` 为 `22/60`；`native-final-pending-close.log` 有实际 `state.savePreferences` 和 `host.finalized ok:true`。
- 客户端无损测试：`r4-evidence/client-close/results.json` 的 `R4-CLIENT-LATEST-PENDING-CLOSE` PASS（18→32，关闭时以 32 为准）。

### R4-02 偏好资格与位置交互资格分离

改动：

- `state.interact` 只返回位置 `interactionToken`，不再承担字段授权，不再返回 `fieldVersion`；偏好输入走 `state.grantPreferences`。
- `reader.js` 设置变化不再触碰位置 token；每个设置变化一个 operation/grant，关闭时等待最后 pending。
- 宿主按 session/window/operation/field/opaque grant 校验偏好；操作 ID 幂等；与位置 token 完全独立。
- grant 队列有 4096 上限，只回收已完成/过期授权，不随机淘汰未完成授权。

证据：

- `H-R4-02-AUTH-SEPARATE` PASS：偏好 grant 前后位置 token 仍为 1，使用 token 1 的 `state.savePosition` 成功。
- `H-R4-02-FIELD-GRANT` PASS：lineHeight grant 不能保存 fontSize；无 grant 的 lineHeight 返回 `MISSING_GRANT`。
- 客户端 fake-host：`R4-CLIENT-AUTH-SEPARATE` PASS，页码操作后改 font 24，位置保存仍用旧 token 且成功。
- 真机 pending close：关闭前的设置保存实际写入 `preferences.json`，不依赖位置 token。

边界：本轮真机“两窗口 A 设置/ B 设置 / A 行高”的 UI 自动化只成功产生 B 的 `fontSize=32` 写盘，A 的输入未落到真实窗口（坐标操作没有命中控件），因此**不把该场景记为真机通过**；对应授权顺序和旧操作拒绝由上述宿主审计、fake-host 客户端测试覆盖。原生单窗口设置+关闭保存通过。

### R4-03 最终关闭语义、pending 与早期关闭

改动：

- `finishAndAck` 设置 `finalizing`，停 debounce/搜索/动画，先等所有 pending 偏好（grant + save），再等 save flush；只有最终保存成功且没有待写状态才回 `ok:true`。
- 关闭时不再把“无位置修改”强行当位置保存，避免无 token 误报失败。
- `state.savePreferences` 的响应失败会被识别；失败时 `finishAndAck` 回 `ok:false`，宿主日志 `CLOSE_ERROR`，不再假装成功。
- 宿主 `host.finalize` 只接受**非空且等于当前 session** 的 `host.finalized` + 匹配 requestId；空/错 session/错 request 不关闭。
- 早期关闭：`_closeStarted` 阻断初始化/导航；`CleanupTemp` 不再停/清 `_closeTimer`；`OnAppWindowClosing` 启动独立的 1500ms 关闭期限。
- 真实旧宿主/客户端能力不足时降级 local 模式，不冒充 host 模式。

证据：

- 真机 pending 设置关闭（最终 host + 0.8.0）：`native-final-pending-close.log`
  - `raw state.savePreferences {"lineHeight":2.1}`
  - `REPLY state.savePreferences ok=True`
  - `raw host.finalized {"ok":true,"revision":4}`
  - `CLOSE_ACK revision=4`
  - 写盘 `native-final-pending-prefs.json` 含 `lineHeight=2.1`。
- 真机早期关闭：`native-early-close.log`
  - `CLOSING request=47f7b08f...`
  - 1.5s 后 `CLOSE_TIMEOUT ... revision=0`
  - 窗口已销毁，无 >10s 悬挂。
- 宿主审计：`H-R4-03-FINALIZE-ASSOC` PASS（缺 session / 错 request 不关闭，正确 session+request 的 `ok:false` 触发关闭）；`H-R4-03-EARLY-CLOSE` PASS（未初始化也可由 deadline 关闭）。
- 客户端 fake-host：
  - `R4-CLIENT-LATEST-PENDING-CLOSE` PASS：首个 18 保存延迟，关闭时 32 仍被写入并 ack true。
  - `R4-CLIENT-FAILURE-ACK-FALSE` PASS：受控写失败时 `host.finalized.ok=false`，无成功假象。
- 真机无修改关闭：`native-nochange-close.log` 有 `host.finalized ok:true` / `CLOSE_ACK`；早期 loading 关闭不再卡死。

### R4-04 位置操作版本、ack 归属和失败快照

改动：

- `flushSaveOnce` 冻结 book/openIntent/opVersion/token/sequence/operationId/baseRevision；旧 ack 只在同一 book 和同一 `saveOpVersion` 时清 `modified`。
- 失败快照保存冻结 token/sequence/operationId/baseRevision；重开时先读盘，如果磁盘 revision 更新则丢弃旧快照；否则重试并把可见位置恢复成快照位置。
- 失败快照不因页面翻页/旧图片 ack 被清掉。
- 宿主位置保存按 token+sequence 校验；旧 token 拒绝，同序号幂等。

证据：

- 真实 Edge C03：`r4-evidence/image-ack/results.json` `C03_OLD_IMAGE_SAVE_ACK_CLEARS_NEW_PAGE`：第一页图片写入延迟，第二页 ack 到达后 `modified:false`，磁盘最终 `pageIndex:2`，没有只存第 1 页。
- 真实 Edge C02：`r4-evidence/races-v2/results.json` `C02_FAILED_SNAPSHOT_REOPEN_VISIBLE_POSITION`：本地保存失败后重开，可见页 `page=4` 与磁盘快照一致，pending 已重试。
- 宿主审计 `H-R4-04-POSITION-TOKEN` PASS：首保存成功、同序号幂等、非法序号拒绝。
- R3 旧图 ack 缺陷（只在第 2 页清 modified）在 R4 C03 下不再复现。

### R4-05 按书损坏隔离、书签校验、空/空白 ID

改动：

- 本地状态文件按书分开：A 书损坏只标 `_bookCorrupt[A]`，不阻断 B；全局偏好损坏单独 `_prefsCorrupt`。
- 打开 A 失败/状态坏时允许 B 正常读；B 的位置/书签/偏好仍可写。
- 书签 id 空白/过长、summary >256、单条 >4KiB、分页 cursor 非法、重复 id 全部校验；重复添加幂等。
- 空/空白 bookId、空/空白 bookmark id 在客户端和宿主双层拒绝。

证据：

- 真实 Edge C01：`image-ack/results.json` `C01_CORRUPT_BOOK_BLOCKS_OTHER_BOOK`：`oldPosition="{bad"` 属于 NORMAL；FRESH 仍有 `newPosition`、书签，`defect:false`。
- 客户端 `R4-LOCAL-BOOKMARK-VALIDATE` PASS：空白 id→`BAD_BOOKMARK`；257 字 summary→`BOOKMARK_TOO_LARGE`；5000 字节实体→`BOOKMARK_TOO_LARGE`；合法 1 条成功；总数 1。
- 宿主审计 `H-R4-05-BOOK-ISOLATION` PASS：写入坏 A 后 B 的 `state.load` 仍返回。

## 2. R4-06 ~ R4-09

### R4-06 资源/任务按书归属与早失败清理

- `prepareSection` 失败时释放本任务资源；`reader` 记录 `navBook`，旧书迟到任务只释放旧书，不误杀新书 owner。
- 资源表/任务表只保留当前任务条目；失败 prepare、旧任务完成、200 次图片章节切换都归一到 1 个当前资源/任务。
- 模型证据：`model/model-results.json` R01/R02/R03/R04/R05/R06 全 PASS；`model/reader-flow-results.json` F06/F07 PASS。
- 真实 Edge C04：`image-ack/results.json` `C04_OLD_BOOK_RELEASE_REVOKES_NEW_IMAGE`：旧书 release 后新书当前 blob 仍可 fetch、资源列表仅新书，`defect:false`。

### R4-07 搜索取消、点击时查询身份、可见模型

- `search()` 改为固定字符 chunk（64KiB + overlap）扫描，零命中/稀疏命中也会按块让出，不再只在命中数处取消。
- 搜索结果携带 `{bookId, openIntent, queryGen, query}` 上下文；点击旧结果、查询已变、换书、查询代次变化时拒绝高亮。
- 可见文本模型：排除 `hidden`、内联 `display:none`/`visibility:hidden`、SVG `display`/`visibility` 隐藏子树、`defs/desc/title/symbol`；隐藏 `<br>` 不产生换行；`aria-hidden` 文本按**视觉可见策略保留**；`<use>` 暂不解析，作为明确边界记录在报告中。
- 模型：S06 零命中/单尾命中/多章 8MiB 均在 17ms 内取消且 timer 已跑；T01/T02/T03 PASS，T03 的 `use` 场景为 OBSERVATION（明确不支持，不是伪造失败/通过）。
- 真实 Edge C05：查询变更后旧搜索点击不再提交旧高亮（`defect:false`）；C06 隐藏 `<br>` 与 SVG `visibility:hidden` 不可见文本不被搜索命中。

### R4-08 ZIP 实际条目 + 精确字体 MIME

- `META-INF/encryption.xml` 字体目标必须：路径在 manifest 中、ZIP 中实际存在、manifest MIME 在明确字体白名单（不能仅字符串含 `font`/`otf`）。
- 支持 IDPF 字体混淆标记 `application/vnd.ms-opentype`、`font/otf`、`font/ttf`、`font/woff`、`font/woff2` 等；非白名单/不存在条目/非 IDPF 全部明确拒绝。
- 模型 `E01/E02` PASS：manifest 写 `application/x-not-font`、`text/x-woff-comment`、伪造 font 路径、只在 manifest 不存在 ZIP 等都被拒；真实 IDPF 字体目标可读并降级系统字体。
- `E03` OBSERVATION：`<use>` / 某些相对 URI 解释属于未覆盖策略边界，报告明确，不当作通过。

### R4-09 能力协商与真基线兼容

- 协议 v2；hello 必须 `protocolVersion=2` 且四项 cap 均为 true：`persistentState`、`preferenceGrants`、`positionOperations`、`finalizeAck`。
- 老宿主/老响应：新客户端忽略 v1/缺 cap 响应，超时降级 local，footer 明确“位置仅保存在当前窗口”。
- 新宿主 + 旧 version1 客户端：宿主只接受 app.ready/book.begin/book.ready/book.error 生命周期，`hello` 回 version1 `INCOMPATIBLE_PROTOCOL`；旧客户端降级 local，不被错误宣称 host 模式。
- 真基线 c12fe262（`index.html` 675 行 / 30,973 bytes / SHA `88FA8B2E0537E535B3430B2D8B6D1C3FF7085F3599CF2CD2813267448939721A`）：R4 宿主有书、无书都打开成功：
  - `compat/r4host-c12-withbook.png`，PID 40896
  - `compat/r4host-c12-nobook.png`，`r4host-c12-nobook-identity.json`
- 原始 host 基线 b978661：`--webview-exe-version=1.1.12+b978661b8a9ec7a9c9fee87d5d323ea1db2bc5ae`，DLL `57CC576547C1FE8BC51780C722E6F39B7F00838A6CF75B19AAAC539DA2AAF3CD`，exe `D0F1...B335`；R4 0.8.0 扩展打开书成功，footer local 降级（`compat/oldhost-b978661-r4ext-*`）。
- R4 宿主 + R3 0.7.0 客户端：`LEGACY_HELLO_INCOMPATIBLE`，页面 local 提示正常；`compat/r4host-r3client-*`。
- PDF 0.3.0 + R4 宿主：`compat/r4host-pdf030-withbook.png`，legacy `lyra-book-ready`，打开成功。
- 真正旧/旧基线仍使用 R3 已接受的补充证据 `docs/epub-optimization/r3-evidence-supplement/compat-baseline-c12fe26/`（未重复造假）；R4 未改动该目录。

## 3. 原 A/F 清单与测试汇总

- 浏览器 A：`reader-smoke.out` 12/12 PASS；`reader-extra.out` 9/9 PASS。
- 解析/搜索/资源模型：`model/model-results.json` 22 项，0 FAIL，1 OBSERVATION（S/T/E 的明确边界）；`model/reader-flow-results.json` 8/8 PASS。
- 独立 GPT R3 回归改写到 R4：
  - `image-ack/results.json` C01-C06，6 行，0 defect
  - `races-v2/results.json` C02/C03，2 行，0 defect
- 宿主源码审计：`host-audit/r4-host-results.json`，7/7 PASS。
- 客户端协议 fake-host：`client-close/results.json`，4/4 PASS。
- 真机最终：
  - 设置落盘：`native-final-pending-prefs.json` / `native-final-pending-close.log`
  - pending 关闭：`CLOSE_ACK`
  - loading 早期关闭：`native-early-close.log` `CLOSE_TIMEOUT` 1.5s，窗口消失
  - 真机身份：`native-final-identity.json`

### 未完成 / 未覆盖（明确边界）

1. **A11 真实 late 图片**、**A22 真实 R2/R3 书**：R4 未扩大真机样本，沿用 R3 已冻结证据；本轮只补 C 系列真实 Edge 和宿主审计，不声称本轮重新跑过。
2. **R4-01/02 真机两窗口设置序列**：UI 自动化只完成 B 的 `fontSize=32` 落盘；A 的字体/行高输入未命中真实控件。该项记为**部分**；授权顺序/旧提交拒绝由 host audit 和 fake-host 覆盖，不能把它写成真机通过。
3. **宿主 I/O 写失败的真正 WinUI 关闭**：本轮的 `ok:false` ack 来自协议 fake-host；宿主审计覆盖 session/request 关联和 `ok:false` 关闭路径，未在真机组策略/ACL 下强制写失败。
4. A21 的旧/旧真基线使用 R3 已接受 addendum，未在 R4 重复跑；R4 只验证了 R4 host/新 client 与真 c12、原 b978661、R3 0.7.0、PDF 0.3.0 的四个交叉边界。
5. 搜索可见模型：`aria-hidden` 按视觉可见策略包含；`<use>`/SVG 符号引用不做解析，`symbol` 模板不作为可见文本；这是明确选择，不是隐藏缺陷。

## 4. 结论

R4-01 ~ R4-09 的代码与 R4 新证据已落盘；R3 冻结物未改动；候选包和宿主候选隔离在 `D:\Temp`。请按 R4 任务书验收；上面“未完成/未覆盖”部分请作为验收边界，不要用本轮没有测到的真机两窗口/真实 I/O 失败推断通过。
