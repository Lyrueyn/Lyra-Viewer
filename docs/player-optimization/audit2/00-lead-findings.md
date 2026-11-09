# Lead 复核记录（第二轮：现场问题调查，2026-10-31）

> 现场：用户安装目录 `D:\Users\Administrator\AppData\Local\LocalMediaPlayer`，跑的是 **1.1.13 + Top3 优化构建**（分支 `opt/player-top3`，HEAD `30f6d3c`）：
> - `dsh_engine.dll` SHA256=`405A7778FE55814E…`，与工作区 `app\dsh_engine\dsh_engine_vsr.dll` 逐字节相同
> - `WinUIGui.dll` SHA256=`1C9B8B4587D8188B…`，与 `D:\Temp\lyra_host_top3_build\WinUIGui.dll` 相同
> - 日志：`dm_debug.log` 6.19MB/85,687 行、`dsh_engine.log` 4.35MB/60,561 行（截至 18:36）、`crash.log` 594 行
> - 两份主日志都**没有日期**：dm 只有 `HH:mm:ss.mmm`；engine 只有 `GetTickCount64` 毫秒。本轮用「前端『切换画质完成』↔ 引擎 `seek jump N ms`」对齐，得 `engine_tick − 258793176 = 当日毫秒`，三次切换独立验证误差 ≤1ms。

---

## A. 切换画质「概率性未响应」（**已确凿**）

### A.1 现场时间线（18:33:30，1080P → 480P）

| 时刻 | 证据 | 内容 |
|---|---|---|
| 18:33:30.272 | `dm_debug.log:85457` | `切换画质 → 480P（位置 3833ms，暂停=False）` |
| 30.277~30.333 | dm L85458-85469 | OpenMedia 同步段（字幕/音量/画质版本/META）≈18ms（**不是**卡点） |
| 30.355 | `dsh_engine.log:60251`（tick 258823531） | `LoadMedia ok 852x480`（加载本身仅 ~63ms） |
| 30.745 | engine L60253（tick 258823921） | `RTX VSR: 已初始化 852x480 → 2787x1570`（**VSR 特性在 UI 线程重建：Rv_Shutdown+Rv_Init，耗时 366~422ms**） |
| 30.379 → 35.79 | dm 43 条 TICK 缺失 | **UI 线程停摆 5.4s**；引擎侧 35.636 打出 `Present 设备丢失 hr=0x887A0005 reason=0x887A0006`（DEVICE_HUNG/TDR） |
| 35.777 | engine L60273 | `★ 设备已重建`（141ms） |
| 35.791 | dm L85471 | `切换画质完成`（此时才 SeekJump(3833)） |
| 35.806 | dm L85476 | `★ C# 渲染回调停摆：已 5.4 秒没触发 OnRenderFrame`（双证） |
| 36.541 | dm | TICK 恢复：pos=3900（已回目标位） |

### A.2 根因链（按证据强度）

1. **【确凿】VSR 特性在 `Present()` 里销毁重建**：`dsh_engine.cpp:2641-2644` 每帧调 `vsr_process_cpu()` → `vsr_prepare()`（`:543-552`）：源尺寸变了（1080P→480P，目标 3840×2160→2787×1570）→ `vsr_release_feature()`（`:515-518`，`Rv_Shutdown` 无日志）+ `Rv_Init`（`:545-552`）。三次切换实测重建耗时 **366/422/375 ms**（LoadMedia ok → `RTX VSR: 已初始化` 的 tick 差）。
2. **【确凿】`Present()` 内的 GPU 等待无界**：`vulkan_video.h:499` `WaitForFences(..., 5s)`（且失败不复位 fence）；`:318/:405` 尺寸变化分支 `vkQueueWaitIdle()` **无超时**。这些全在 UI 线程。
3. **【确凿】本次 TDR 路径**：上一画质 4K VSR 的 GPU 工作还没落地，就 `Rv_Shutdown`+重建 → 驱动 TDR（`DEVICE_HUNG`）→ `Present` 卡 5.257s（与 5s fence 同量级）→ 重建 141ms → 合计 5.41s → Windows 判「未响应」。
4. **【推断】为什么是概率**：是否撞 TDR 取决于切的一瞬间 GPU 队列里是否还有上一代 VSR 的工作在飞；第 2/3 次切换（40.79→41.36、42.74→43.21）只卡 0.45~0.48s，没有 TDR。
5. **【已排除】Top3 不是回归源**：`git show c8d3f06` 只动了 `qCap/qPush/DecodeAhead` 顶部判据、`dsh_clock_ms_double` 的 `!fileEnded()` 和 3 个新导出，未碰 `LoadMedia/DoSeek2/Present/SwitchQualityAsync`；且 `QMAX=16` 早已把 `qCap()` 封顶，1080p 前后都是 16（只有 4K 5→9）。

### A.3 第二条潜在卡死路径（本次未触发，但存在）
`Present()` 用阻塞 `lock_guard(g_mtx)`（`dsh_engine.cpp:2599/2619`），`LoadMedia()` 全程持锁（`:2033`），锁内含 `avformat_open_input/find_stream_info/avcodec_open2` 和 `g_audio.openFile()`（`:2071`，其中 `audio_out.h:155` 有 `i<400 sleep(10ms)` 等设备就绪，**最多 4s**）。本次该段 63ms，但独占/设备被占用时就是现成的 4s 卡死。

---

## B. 切换画质「卡顿出现一帧开头」（**已确凿**）

1. **新文件从 0 出帧**：`LoadMedia` 末尾 `lastPtsUs=0`（`:2085`）、主时钟显式锚定 0（`:2114`）、`mediaLoaded=true`（`:2115`）→ 解码线程立刻出 0ms 帧；前端此时还在 `SwitchQualityAsync` 的轮询里（`VideoWindow.xaml.cs:2986-2991`，`for(60){await Delay(100); if(GetDurationMs()>0) break;}`）→ 实测「开头帧曝光」**445/476 ms**（≈片头 11 帧 @24fps）；TDR 那次被放大到 **5.42s**。
2. **回位 seek 后又闪一帧更早内容**：`DoSeek2` 置 `queueFirstAfterSeek=true`（`:2159`），`DecodeAhead` 让落点关键帧先上屏（`:2239-2247`）。日志：目标 7267ms→落点 5033ms（差 2234ms）、目标 8600ms→落点 5033ms（差 3567ms），随后才「追赶 67/107 帧」，即先看 2.2~3.6s 前的内容约 50~100ms。
3. **不是 Top3 回归**（同上）。

---

## C. 视频加载时间长 + 启动后几帧必卡一次（**已确凿，量化如下**）

### C.1 每次打开视频的耗时拆解（18:36 两次打开实测）

| 阶段 | 证据（engine tick / dm 时间） | 耗时 |
|---|---|---|
| 窗口创建 → 引擎 Init（D3D11 设备+交换链） | 258987234→258987265（dm 14.03~14.07） | **31ms**（很便宜） |
| **RTX VSR 探测（LoadLibrary nvngx + `Rv_Probe`）** | 258987281→258987734（dm 14.09~14.54） | **453ms**（第 1 次打开 594ms；**每次开视频窗都做**） |
| LoadMedia（解封装/解码器/音频/纹理） | 258987750→258987765 | **15ms**（文件加载本身不慢！） |
| 弹幕层 FreeType 初始化+图集（**建了两次**） | 258987750 与 258987796（尺寸 2288×1408 → 2787×1812） | ≈46ms + 两次图集分配 |
| Vulkan 共享目标创建 | 258987796→258988…（首帧前） | 141~250ms |
| 首帧 TICK | dm 14.766（窗口创建 13.978 起） | **合计 ≈0.8s**（第 1 次打开 08.074→09.300 = **1.23s**） |

结论：**「加载慢」的主项不是文件解码，而是 VSR 的 NGX 探测（≈450~600ms，同步在 UI 线程）**，其次 Vulkan 目标（≈150~250ms）、弹幕层重复重建（≈50ms）、前端同步字幕/元数据（≈200~300ms）。每次开窗还无条件 `CopyRtxDllsToApp` 复制 `nvngx_vsr.dll`(19MB)+`nvngx_truehdr.dll`(4MB)（`VideoWindow.xaml.cs:495-511`，每次 File.Copy）。

### C.2 「启动后几帧必有一次卡顿」——**100% 可复现的 VSR 延迟重试冻结**

证据（同一机制两次）：

| 打开 | 首次尝试 | 重试时刻 | UI 冻结 |
|---|---|---|---|
| 第 1 个视频 | 18:36:08.913 `ApplyRtxVsrOnOpen 首次失败 rc=-5 → 1.2 秒后重试` | 18:36:10.113 触发 → 10.514 返回 | **≈400~500ms**（TICK 10.015→10.514） |
| 第 2 个视频 | 18:36:14.569 同样 rc=-5 | 15.769 触发 → 16.434 返回 | **≈660ms**（TICK 15.766→16.434，缺 5 个 TICK） |

机制：`OpenMedia` 立即调 `ApplyRtxVsrOnOpen()`（`VideoWindow.xaml.cs:664-696`）→ 引擎媒体还没加载（`g.vw=0` → `vsr_calc_target` 返回 false → `vsr_prepare` 返回 false）→ `DshEngine_SetRtxVsr(1)` 返回 **-5**；前端 `RetryRtxVsrOnOpenAsync` 1.2s 后在 **UI 线程**再调 `SetRtxVsr(1)` → 引擎 `vsr_prepare()` 做 `Rv_Init`（NGX 特性 + 输出纹理，最大 3840×2160）+ **立刻把最后一帧跑一遍 VSR**（`dsh_engine.cpp:2530-2535`：`UpdateSubresource` + `vsr_eval_frame`）→ 冻结 0.4~0.66s。
**因为用户设置里 `rtx_vsr=true`，所以每次打开视频都必然发生一次**——这就是「启动后几帧必有一次卡顿」。

---

## D. 关闭扩展页面耗时较长（来自子审计 04，交叉核对过代码）

1. **【确凿】关闭被实现成「先取消 → 等扩展 finalize 回执 → 再第二次 Close」**：`ExtensionHostWindow.xaml.cs:570` `args.Cancel = true` → `:573-575` `host.finalize` → `:578` 起 **1500ms** 超时定时器 → `FinishFinalizeAndClose`（`:581-590`）。窗口在整个等待期一直留在屏幕上 → 全部等待都变成肉眼卡顿。
2. **【确凿】PDF 扩展 0.3.0 没有实现 `host.finalize`**：`extensions\pdf\reader.js` 全文只有 1 处 `postMessage({type:"lyra-book-ready"})`（第 23 行），无 message 监听、无 finalize 应答 → **每次关 PDF 恒定吃满 1500ms**。
3. **【确凿】EPUB 正常很快，但有最坏 4.3s 路径**：`reader.js:1850-1863` 三个 bounded(2800ms)+1200ms；`:1814` `if (state.finalizing) return;` 提前返回时不发回执 → 必定等满超时。历史 `final-host-debug.log` 里 CLOSING→CLOSE_ACK 稳态 1/8/2ms，说明慢主要在 PDF/异常路径。
4. **【确凿】清理在关闭路径上同步跑 UI 线程且失败**：`:88` Closed → `CleanupTemp()` → `:658` `Directory.Delete(_tempDir, true)`；WebView2 仍持有目录映射 → 抛异常被吞、`_tempDir=null` 永不重试。实测 `D:\Temp\lyra_ext_host` 残留 **102 目录 / 3,614 文件 / 295MB**。
5. **【推断】WebView2 无显式释放**（两文件全文无 Dispose/CoreWebView2.Close）。
6. **【确凿】生产零埋点**：`LogHost`（`:592-598`）只在 `LYRA_READER_DEBUG=1` 时写 `reader_host_debug.log`，安装目录没有该文件 → 现场关闭耗时的真实分布无法量化。

已排除：`ReaderStateStore` 是纯本地文件（WriteAllText+Flush(true)+File.Replace，`Thread.Sleep(10)` 重试），**不走后端 HTTP**，无 15 分钟超时问题；没有 `Task.Wait/GetAwaiter().GetResult()`；MainWindow 没有 EnableWindow 冻结。

---

## E. EPUB 目录序号从左侧溢出（待与子审计 03 合并）

Lead 先做的排除：
- 安装中的 EPUB 扩展 = **v0.6.1**（`extensions\epub\extension.json`），侧栏目录（`buildTocList`，`reader.js:608-638`）只渲染 `▾ + 标题 + paddingLeft 缩进`，**没有序号**；`reader.css:39-44` 也没有 counter/::before → 「侧栏目录序号」在当前版本不存在。
- 因此用户说的「目录左侧序号」最可能指 **正文里的书内目录页**（EPUB 自带 `<ol>/<ul>` 目录列表）：正文由 `paginator.js` 的 `CONTENT_CSS`（第 8-47 行）渲染，列表样式只有 `#lyra-content ul, #lyra-content ol { margin:0 0 1em; padding-left:1.5em; }`（第 36 行），浏览器默认 `list-style-position: outside`；而正文是**横向分页的 CSS 多列**（`:20-24` column-fill:auto + overflow 隐藏），序号画在列内容盒左外侧，1.5em（22px 字号≈33px）装不下三位数序号（"100."≈45px）或大字号时，就会被裁到左边界外/上一列 → 表现为「序号从左侧溢出」。
- 历史线索：0.5.23 曾修过「侧栏长标题不向左溢出」（commit 9bc422f：`#side{overflow-x:hidden}` + `.chapter{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}`）；0.6.0 重写成 iframe 分页后那套外壳样式不再适用于正文，属于**重写造成的回归面**。
- 修复方向：`#lyra-content ol,ul { padding-left: 2.6em !important; }` + 对大编号/大字号更稳的 `list-style-position: inside`（或给列容器加左内边距）；并加回归用例（三位数字列表、字号 36、窄窗 600px、深浅主题）。

> 待子审计 03 用扩展仓库测试/截图确认具体书名/元素后回填。
