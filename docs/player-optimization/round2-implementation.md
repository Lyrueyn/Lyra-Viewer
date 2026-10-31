# 第二轮现场问题修复记录（2026-10-31）

分支：`opt/player-round2`，提交 `1c49615`（基于 `opt/player-top3@30f6d3c`）
状态：**本地测试构建已装入安装目录；未合并 main、未发安装包、未改 GitHub Release**。

## 1. 已改内容

### 1.1 切画质「未响应」/ GPU 等待 —— 先做安全止血

- 新增 `DshEngine_SuspendRtxVsr()`：切画质前只清 VSR 开关，**不在 Present 里 Rv_Shutdown**，避免旧 VSR GPU 工作在飞时销毁 NGX 特性；切完由后台重新初始化。
- `SwitchQualityAsync` 在 `OpenMedia` 前调用该接口；失败也不影响继续切。
- 未把 VSR 生命周期完全移出 Present（那是更大改动）；本轮先消除「切画质时主动销毁重建」这一最可疑触发路径。
- `Present` 的 5s fence / `vkQueueWaitIdle` 边界本轮**没有改**；需要用户实测 TDR 是否还复现，再决定是否动等待。

### 1.2 闪开头帧 —— `OpenMediaAt` + 遮罩

- 引擎新增 `DshEngine_OpenMediaAt(path, startMs)`（ABI 3）：`LoadMedia` 完成前先 `avformat_seek_file` + `avcodec_flush_buffers`，把时钟/`lastPtsUs`/音频都锚到 startMs；`skipsToClock=true`、`queueFirstAfterSeek=false`，落点前帧不进入队列。
- 前端 `SwitchQualityAsync` 改调 `OpenMedia(path, startMs: pos)`，删除「等 6 秒再 SeekJump」的轮询。
- 视频层新增 `QualitySwitchMask` 黑遮罩，切换期间盖住旧画面，280ms 后撤。

### 1.3 加载慢 / 启动必卡 —— NVNGX 缓存 + VSR 后台初始化

- `InitRtxVsrUi`：本进程内用 `_rtxSdkAppliedSig` 记住 SDK 路径 + `nvngx_vsr.dll`/`nvngx_truehdr.dll` 的大小/修改时间；未变化跳过 `CopyRtxDllsToApp` 和 `SetRtxSdkPath("")`，直接走引擎缓存探测。
- 引擎新增 `DshEngine_IsMediaReady()`（ABI 3），前端不再在媒体未就绪时制造必然的 `SetRtxVsr -> -5`。
- `ApplyRtxVsrOnOpen` 改为后台任务：等媒体就绪 → `Task.Run(SetRtxVsr(1))` → 回 UI 线程更新开关/状态；不再在 UI 线程做 400~600ms 的 `Rv_Init`。
- 引擎新增 `g_vsrMtx`/`g_vsrBusy`：后台 `Rv_Init` 期间 Present 看到 busy 就跳过本帧 VSR，不让 UI 等锁。
- 删掉 `SetRtxVsr` 里「初始化完立刻跑最后一帧 VSR」的 UI 线程开销；下一帧 Present 自动应用。

### 1.4 关扩展慢 —— 先隐藏、旧扩展免等、清理后台

- 关闭时先 `AppWindow.Hide()`，窗口立即消失；finalize 在后台继续。
- 收到过当前协议消息的扩展（新版）最多等 **250ms** 回执；PDF 0.3.0 这类从未发过协议消息的扩展直接关，不再固定等 1500ms。
- `CleanupTemp` 改为 `Task.Run` + 10×50ms 重试删除，不在关闭路径同步 `Directory.Delete`。
- `Closed` 时先 `CoreWebView2.Stop()`，再后台清理。

### 1.5 EPUB 目录溢出 —— 0.6.2 本地修复

分支 `fix/toc-overflow` 提交 `970e662`：

- `.side-scroll`：`overflow-x: hidden` → `clip`，不再产生可横滑的滚动容器。
- `.toc-item` 增加 `white-space: normal`，覆盖全局 `button{nowrap}`，长目录标题换行。
- `paginator.js` 正文 `ul/ol`：`padding-left: 1.5em` → `2.6em`。
- 版本 0.6.1 → 0.6.2，`index.html` 所有 `?v=` 同步。

### 1.6 PDF 关闭握手 —— 0.3.1 本地修复

分支 `fix/finalize-handshake` 提交 `8a56e8a`：

- PDF `reader.js` 监听 `host.finalize`，回 `{type:"host.finalized", requestId, sessionId, ok:true, revision:0}`；`sessionId` 从 URL 取。
- `extension.json` 版本 0.3.0 → 0.3.1。

### 1.7 EPUB 翻页冷却 / 动画串行 —— 0.6.3 → 0.6.8（追加）

分支 `fix/toc-overflow` 提交 `dfb54e7`、`2ce9012`、`81d141e`、`7442063`、`963e1ff`、`93acb0c`：

- `paginator.js` 把翻页动画时长常量化为 `TURN_ANIM_OUT_MS=90` + `TURN_ANIM_IN_MS=130`，总 220ms，并导出 `TURN_ANIM_MS`。
- `reader.js` 计算 `TURN_COOLDOWN_MS = TURN_ANIM_MS / 3 ≈ 73ms`；`userTurn` 在冷却期内直接丢弃下一次翻页，不再排队。
- 用户反馈“快速点击时极少数页无动画”后，追加串行化：`processTurns` 按完整动画时长（220ms+40ms 余量）等待后再处理下一次翻页，避免连续 `cancelAnimation` 吞掉中间页动画。
- 减少动画模式下没有动画可打断，冷却为 0。
- 用户又反馈“点几下就翻不了页”：确认是等动画 Promise 在 rAF 被暂停时会永久挂起、`turnBusy` 卡死；改为纯计时器串行（不依赖 rAF Promise），并保留 1/3 冷却。\n- 用户继续反馈“翻页越来越快/进第二章第一下特别快/停手后还继续翻”：取消队列积压，动画进行中的点击最多保留 1 个；`navigateToSpine` 等章节淡入完成后再返回，避免第二章第一页的切章动画被下一次翻页 cancel。\n- 用户再反馈“翻页冷却过长”：原 220ms 动画确实偏慢。把翻页动画缩到 **100ms（40+60）**，冷却缩到约 33ms；不再提前定格当前页（那会造成“动画消失再出现”），改为每页完整 100ms 淡出/淡入。\n- 扩展版本 0.6.2 → … → 0.6.7 → 0.6.8，`index.html` 缓存版本同步。
- 新增回归 A12b（动画中点击丢弃、空闲后可继续）与 A12c（140ms 间隔连点 5 次全部前进、停手后队列为空、busy 结束）；smoke **14 项全 PASS**。

## 2. 构建与安装

- 引擎：`dsh_engine_vsr.dll` 5,248,091 bytes  
  SHA-256 `71161597E616262315CDA97F62B04EA17DFFB6E3AF55B90D3ED280E447E8259F`
  - 导出验证：`OpenMediaAt`、`IsMediaReady`、`SuspendRtxVsr`、`GpuDead`、`GetApiVersion`、`GetEndFlags` 均在。
- 主程序 Debug：`D:\Temp\lyra_host_round2_rebuild\WinUIGui.dll`  
  SHA-256 `8E8C4B262682BC0CD6BD672187759982DF71DC9284F9E8770B9F7F47EF2DDD08`
  - 0 errors。
- 已把上述 host/engine + EPUB 0.6.8 + PDF 0.3.1 装入：
  `D:\Users\Administrator\AppData\Local\LocalMediaPlayer`
- 旧文件备份：`D:\Temp\LocalMediaPlayer-installed-backup-round2-20261031`
- 版本号仍为 1.1.13（本地热更新，不冒充新版本；未发安装包）。
- TXT 阅读器仍为 0.1.0，未改、未提交。

## 3. 已验证

| 项 | 方法 | 结果 |
|---|---|---|
| `OpenMediaAt` 起点 | 独立 `test_openat.exe`（`HOST_START_MS=3000`） | 日志 `OpenMediaAt 起点 3.000s`；首轮 `GetPosMs=4067`，不是 0 帧；证据 `round2-evidence/openmediaat-test.log` |
| VSR 后台初始化 | 测试目录 `--video testclip` | `Rv_Init+准备 404.3 / 433.6 ms` 由后台线程完成，日志有 `[性能]`；UI 不再直接执行该段 |
| 安装版 ABI | 安装目录启动 `--video` | 标题正常，无 ABI 不匹配；截图 `D:\Temp\installed-round2-video.png` |
| 扩展关闭 | 安装版 UIA 打开 EPUB/TXT 等扩展窗口后 `WindowPattern.Close` | 实测窗口消失 **130~137ms**，剩余窗口 0；PDF 0.3.0 路径走 legacy 免等 |
| EPUB 回归 | `tests/reader-smoke.cjs` | 12 项 PASS / 0 失败；证据 `round2-evidence/epub-smoke.log` |
| PDF 语法 | `node --check reader.js` | 通过 |

## 4. 未验证 / 仍需用户实测

- **真实切画质 1080P↔480P↔4K 各 20 次**：本轮没有现成的多画质素材，未做 TDR A/B；VSR 不再在切画质时主动 Shutdown 是否就能消除 TDR，需要现场验证。
- **Present 的 5s fence / vkQueueWaitIdle 未改**：如果切画质仍卡 5s，下一轮才动等待边界。
- **真实 4K VSR 启动**：只测了 852×480→2787×1570；4K 的 `Rv_Init` 可能更久，但已不在 UI 线程。
- **EPUB 目录长标题/三位序号的实际截图**：只跑了原有 smoke（短 TOC）；CSS 修复逻辑明确，但用户那本书的侧栏/正文目录还需现场确认。
- **PDF 完整 finalize 回执**：PDF 0.3.1 已实现，但宿主因 PDF 未发协议消息仍走 legacy 免等；真正 250ms 等待路径主要给 EPUB 等新版扩展。
- 版本号/安装包/发布均未推进；如需 1.1.14 安装包，需要另做版本号、打包、上传。

## 5. 证据文件

- `docs/player-optimization/round2-evidence/openmediaat-test.log`
- `docs/player-optimization/round2-evidence/openmediaat-start3000.bmp`
- `docs/player-optimization/round2-evidence/vsr-background-init.log`
- `docs/player-optimization/round2-evidence/epub-smoke.log`
- `docs/player-optimization/round2-evidence/epub-turn-cooldown-smoke.log`
- `D:\Temp\installed-round2-video.png`

结论：**第二轮 5 个问题的代码修复已落地、编译通过、关键机制有本地证据；但切画质 TDR、真实 4K、EPUB 长目录这三项需要用户现场实测后才能算闭环。**
