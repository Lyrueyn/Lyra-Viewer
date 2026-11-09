# WinUI 播放器前端审计报告

> 审计对象：`app/WinUIGui`（C# .NET 10 / WinUI 3）+ `app/dsh_engine`（P/Invoke）当前线 `main`@`b978661`。
> 只读审计，未修改任何源码/配置/数据，未启动播放器与后端。
> **实测行数与任务书不一致**：`VideoWindow.xaml.cs` 6018 行（任务书 5724）、`VideoWindow.xaml` 976（938）、`MainWindow.xaml.cs` 2090（1973）、`ChainWindow.xaml.cs` 1570（1502）。本报告行号以**实际文件**为准。

## 0. 一句话结论

历史白屏事故（跨片残留 → 每 0.5s 重开 → 引擎拆建 42 次）**确已修好且有四道闸门**；但播放窗口缺"全屏/进度记忆/键盘"三项标配，UI 线程上堆了同步文件 IO 与 5 秒级端口探测，引擎导出与 C# 声明已不同步——尤其**GPU 判死时唯一的恢复按钮「重新启动播放器」实际只会关掉自己**，这是最该先修的一条。

## 1. 现状结构摘要

`VideoWindow`（6018 行、单文件）承担了远超"播放窗口"的职责：P/Invoke 桥（`DshNative`，42 个导出）、渲染循环（`CompositionTarget.Rendering` 逐帧 `Present`）、20Hz 进度/EOS 状态机、弹幕、字幕、HDR/独占/RTX VSR、增强任务、增强链节点图、AI 字幕、评论/简介/信息 6 个 tab。进程模型：主窗 + 命名 Mutex 单实例，`--video <path>` 绕过互斥创建独立播放进程（每进程独立引擎全局单例）。定时器共 7 个（50/120/800/1000/1500/2000/2500ms）外加一个逐渲染帧回调。XAML 单窗口 976 行、`ControlBar` 52px 一行塞 13 个控件。

## 2. 问题清单

### P0-1 「重新启动播放器」= 关掉播放器且不重开（白屏事故唯一恢复路径失效）

**证据**
```csharp
// VideoWindow.xaml.cs:2756-2761 —— 起新实例时【没带 --video】
System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
    FileName = exe, WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = true, });
// :2773-2778 —— 按进程名找"新实例"，主窗口进程也会被当成新实例
foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
    if (p.Id != self) { newPid = p.Id; break; }
// :2807
Application.Current.Exit();
```
```csharp
// App.xaml.cs:98-113 —— 没 --video 就走单实例锁，锁失败直接 Exit(0)
for (int i = 1; i + 1 < argv.Length; i++) if (argv[i] == "--video") { ... return; }
if (!TryAcquireSingleInstance()) { Environment.Exit(0); return; }
```
**影响**：该按钮只在 `GpuDeadBanner` 内（`VideoWindow.xaml:148-174`），文案承诺"会自动开好新窗口再关掉当前这个"。多窗模式：新进程撞 Mutex 立即退出，而 `GetProcessesByName` 在第一个 100ms 轮询就能看到**主窗口进程** → 判定"新实例已启动" → `Exit()` → 视频窗消失、什么都没重开。非多窗模式：视频窗与主窗同进程，`Exit()` 关掉整个程序。**DEVICE_REMOVED 白屏时用户点了唯一能点的按钮，结果是程序没了。**

**根因**：拉起参数缺 `--video`；"新实例是否起来"用进程名判定而非窗口/参数判定。
**方案**：① `ProcessStartInfo.ArgumentList` 加 `--video` + `_currentPath`（与 `MainWindow.xaml.cs:106-107` 同一写法）；② 判活改为「存在标题以 `璃鸢镜 · ` 开头且 PID≠self 的窗口」或由新实例写一次性握手文件（PID+启动时刻），超时即判失败；③ 失败分支已有（2780-2788），保持不变。
**验收**：开视频 → 人为触发判死（`DSH_TEST_GPU_DEAD` 或拔驱动模拟）→ 点按钮 → 5s 内出现新窗口且停在**同一文件同一位置**；旧窗消失；主窗口存活。
**工作量**：0.5 人日（含回归）。

### P1-1 无全屏（视频窗完全没有）

**证据**：全仓 `grep -i fullscreen` 只命中增强链画布 `_chainFullscreen`（`VideoWindow.xaml.cs:1935,2656-2687`）与 `VideoWindow.xaml:831` 的「全屏编辑」；`AppWindow.SetPresenter` / `OverlappedPresenter` 零命中，控制条 13 个按钮里没有全屏（`VideoWindow.xaml:74-136`）。
**影响**：本地播放器最基础的能力缺失，用户只能手动最大化。
**方案**：`PinBtn` 右侧加「⛶ 全屏」，`AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`；`F11`/双击视频区/`Esc` 退出；全屏时隐藏顶栏与右侧面板。
**验收**：F11 进出全屏 <100ms，全屏下进度条仍在；双击=全屏、单击=暂停（需 250ms 单击延迟判定）。
**工作量**：1 人日。

### P1-2 无播放进度记忆 / 续播

**证据**：`grep -i 'resume|last_pos|playback_pos|SaveProgress|续播'` 在 `VideoWindow.xaml.cs`/`Backend.cs` 零命中（唯一"续播"是 `:3009` 注释指控制条计时）。`player_settings.json` 的键共 14 个（`Backend.cs:109-593`），无位置类键。
**影响**：关窗即丢失观看位置，长视频/连播场景体验断层。
**方案**：新增 `data/play_positions.json`（LRU 500 条）；`_seekTimer` 里每 5s 落一次内存，关窗/换片时写盘；`OpenMedia` 里等 `GetDurationMs()>0` 再 `SeekJump(pos)`（复用 `SwitchQualityAsync:2928-2932` 的等待写法并加 `_mediaGen` 校验）；续播 >3s 且未播完才生效，`AutoStopBar` 提示「已从 12:34 继续」3s 自动隐藏。
**验收**：播到 5:00 关窗 → 重开同一文件落在 5:00±0.5s；播完（EOS）后重开从头开始。
**工作量**：2 人日。

### P1-3 键盘只有空格，且 Space 可能双触发

**证据**：
```csharp
// VideoWindow.xaml.cs:3013-3016 —— 全文件唯一一处键盘处理
private void Video_KeyDown(object sender, ...) {
    if (e.Key == Windows.System.VirtualKey.Space) { TogglePause(); e.Handled = true; } }
```
`grep 'VirtualKey\.'` 全文件仅此一条。宿主是 `VideoWindow.xaml:13` 的 `RootGrid`（`IsTabStop="True" KeyDown="Video_KeyDown"`），而构造里 `:249` 每次 `Activated` 都把焦点抢回 `RootGrid`。
**影响**：无 ←/→ 快退快进、无 ↑/↓ 音量、无 M 静音、无 F 全屏、无 Esc；且当焦点落在控制条某个 Button 上时按空格，Button 自身先响应 Click，事件再冒泡到 `RootGrid` → **一次空格既点按钮又切暂停**（推测，需实测）。
**方案**：`Video_KeyDown` 改 `switch(e.Key)`：Space/←→(±5s，Shift=±30s)/↑↓(±5%)/M/F/Esc；开头加 `if (e.OriginalSource is Button or Slider) return;` 避免与控件抢键；不再在 `Activated` 里抢焦点，改由 `Video_Tapped` 显式 `RootGrid.Focus()`。
**验收**：连按 5 次空格状态翻转 5 次且按钮不被误触发；←/→ 每次跳 5s，进度条与画面同步。
**工作量**：0.5 人日。

### P1-4 UI 线程上的同步阻塞（打开媒体链路）

**证据**（`OpenMedia`，全程同步、跑在 UI 线程）：
```csharp
// :5430-5443 一次调用里连做 8 件事
int mrc = DshNative.DshEngine_OpenMedia(path);
_ = UpdateMetaAsync(path, gen);
ApplySpeed(); ApplyExclusive(); ApplyRtxVsrOnOpen();
ApplyVolume(save: false);        // :5437
TryLoadSubtitleFor(path);        // :5440 → LoadSubtitleFile → File.ReadAllText(:3573) + Directory.GetFiles(:3600/3839)
ApplyVolume(save: false);        // :5443 ★ 与 5437 完全重复
```
```csharp
// Backend.cs:28-40 + :73-77 —— 端口探测每次最多 250ms × 20 次
var ar = t.BeginConnect("127.0.0.1", port, null, null);
bool ok = ar.AsyncWaitHandle.WaitOne(250);
for (int k = 0; k < 20; k++) if (!IsPortBusy(8765 + k)) { port = 8765 + k; break; }
```
`MainWindow.xaml.cs:165` 在构造函数里同步调 `Backend.Start()`；`:1311/1416/1573/1586` 又各调一次。
**影响**：大 ASS 字幕（数 MB）+ 同目录数百文件 + 端口被占时，最大可卡 UI 数秒。
**方案**：① 删 `:5443` 重复 `ApplyVolume`；② 字幕查找+解析改 `Task.Run` 后 `TryEnqueue` 回填（`_subCues` 只在 UI 线程写，用临时 list 交换）；③ 端口探测改 `ConnectAsync` + `WhenAny(…, Delay(250))` 并缓存结果；④ `MainWindow` 的 `Start()` 移到 `Loaded` 之后。
**验收**：冷启动到主窗可交互 <1.5s；开 2MB ass 视频窗口仍可拖动（Profiler 无 >50ms 单帧）。
**工作量**：1.5 人日。

### P1-5 引擎导出与 C# 声明不同步，缺导出被当成"没有 N 卡"

**证据**：C# 声明 6 个 RTX 导出（`VideoWindow.xaml.cs:90-101`），直接扫二进制得到：
| 二进制 | Rtx 导出 |
|---|---|
| `app/dsh_engine/dsh_engine.dll`（5238318B, 10-18） | **0 个** |
| `app/dsh_engine/dsh_engine_vsr.dll`（5246621B, 10-23） | 4 个（缺 `GetRtxVsr`、`GetRtxVsrInfo`） |
而源码**是全的**（`dsh_engine.cpp:2468,2497,2498,2530,2531,2537`），`deploy.ps1:64-69` 也只编 `dsh_engine.cpp danmaku_lanes.cpp zstub.cpp`。→ **仓库里的 dll 是旧构建**。
```csharp
// :574 缺导出 → EntryPointNotFoundException → :609 catch 把可用性置 0
_rtxVsrAvail = DshNative.DshEngine_RtxVsrAvailable();
// :582 于是界面这样告诉用户（与事实不符）
RtxVsrDetailText.Text = "需要 NVIDIA RTX 20 系以上显卡 + 驱动 R550.50+，本机未检测到可用的 RTX 显卡。";
```
另：`:659` 的 `DshEngine_GetRtxVsr()==1` 与 `:703-704` 的 `GetRtxVsrInfo`/`GetRtxVsr` 在两个 dll 里都不存在，异常分别被 `:688` 的日志 catch 和 `:711` 的**空 catch** 吞掉，只有副作用（重试逻辑永远走不到"已开着"的提前返回）。
**影响**：DLL 版本漂移时，界面把"接口不存在"误报成"你的硬件不支持"，用户会去换显卡而不是升级程序。
**方案**：新增 `DshNative.Probe()`：启动时对每个导出做 `NativeLibrary.TryGetExport`（.NET 10 原生 API，不必 try/catch P/Invoke），生成 `EngineCapabilities` 位集；所有新接口调用前先查位集；缺失时显示"引擎版本过旧（缺少 X），请重新部署"。
**验收**：把旧 `dsh_engine.dll` 放回 bin → 设置页显示"引擎版本过旧"，且不出现"未检测到 RTX 显卡"。
**工作量**：1 人日。

### P1-6 日志无界且写在高频路径上

**证据**
```csharp
// :183-191 同步整文件 AppendAllText，无上限、无轮转
internal static void DmLog(string msg) {
    string p = Path.Combine(AppContext.BaseDirectory, "dm_debug.log");
    File.AppendAllText(p, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
}
// :4962 逐渲染帧调用；60fps → 每 0.5s 一条，120fps → 每 0.25s 一条
if (++_dmLogTick % 30 == 0) DmLog($"TICK pos=... active={n} ...");
// :5298 倍速滑条每变一格一条；:3329 音量滑条同样每次 ValueChanged 一条
DmLog($"倍速滑条: 设为 {_speed:0.00}×");
```
调用点总数：`VideoWindow` 194 处、`ChainWindow` 27、`MainWindow` 15。引擎侧另有并行无界日志：`dsh_engine.cpp:1986` 每 60 次 `GetDanmakuLayout` 打一行，而该函数**每帧**被调（`:4961`）→ 引擎日志 1 行/秒。
**影响**：2 小时播放仅 TICK 就 ≈1.4 万行 / 2MB，拖两次音量再叠几百条；UI 线程每秒 2-4 次 open/write/close，日志无上限增长。
**方案**：① `DmLog` 分级（`DSH_LOG` 控制，默认关掉 TICK/音量/倍速，只留状态迁移与错误）；② 改 `StreamWriter`（1s flush）+ 10MB 轮转；③ 高频项改"变化才记"（`_lastLoggedVol`/`_lastLoggedSpeed`）。
**验收**：播放 10 分钟日志增量 <200KB 且无 TICK 行；拖音量全程只 1 条日志。
**工作量**：0.5 人日。

### P1-7 `InitGpuDeadWatch()` 里塞了增强器/增强链的初始化

**证据**
```csharp
// :306-331 方法名与内容完全无关
private void InitGpuDeadWatch() {
    _gpuDeadTimer.Interval = TimeSpan.FromSeconds(1);
    EnhancerList.ItemsSource = _enhancers;              // :310 ★ 增强器列表
    ChainRowList.ItemsSource = _chainRows;              // :314 ★ 增强链产物树
    ChainCanvas.PointerPressed += ChainCanvas_PanPressed;   // :319 画布平移
    _gpuDeadTimer.Tick += (_, _) => {
        CheckGpuDead(); RefreshExclusiveStatusLight();
        if (_enhanceTabVisible && (++_enhanceTick % 2) == 0) _ = RefreshEnhanceAsync();   // :326 HTTP
        if (_chainTabVisible && (_enhanceTick % 2) == 0) _ = RefreshChainRowsAsync();     // :328 再一个 HTTP
    };
}
```
**影响**：① 增强器/链初始化若抛异常，会连累"显卡判死看门狗"注册不上 → 白屏时彻底无提示；② 1 秒定时器兼职 UI 刷新与两个 HTTP；③ `_enhanceTick` 只在增强 tab 可见时自增，链页分支却用同一计数器做模 2 → 链页刷新节奏依赖增强页是否开过（可复现）。
**方案**：:310-322 拆到 `InitTabWiring()`（构造函数调用）；`_enhanceTick`/`_chainTick` 拆成两个计数；HTTP 刷新改 3s 且按 tab 可见性启停计时器。
**验收**：断开引擎 dll 启动不抛异常且判死时横幅仍出现；链页停留 6s 自刷新 2 次（与是否进过增强页无关）。
**工作量**：0.5 人日。

### P1-8 多窗模式下「回到主界面」是死按钮

**证据**
```csharp
// App.xaml.cs:28 —— Main 只在主窗分支赋值
public static MainWindow? Main { get; private set; }
// App.xaml.cs:103-107 —— --video 分支建完窗口就 return，Main 保持 null
var vw = new VideoWindow(); _window = vw; vw.Activate(); vw.OpenMedia(argv[i + 1]); return;
// VideoWindow.xaml.cs:5454-5455 —— 于是这里静默返回
var main = App.Main; if (main == null) return;
```
**影响**：`--video` 独立进程里（即"多视频窗口"的默认模式）点「回到主界面」毫无反应，也不写日志。注释 `:5449` 还写着"以前这个按钮没有 Click，纯装饰"——实际仍半装饰。
**方案**：`main == null` 时退化为"按标题 `璃鸢镜` 找主窗并 `SetForegroundWindow`"（复用 `App.FindWindow`，`:58`）；仍找不到则按钮置灰 + ToolTip "主界面未运行"。
**验收**：多窗模式点按钮 → 主窗前置；杀掉主窗后按钮置灰。
**工作量**：0.3 人日。

### P2 级（合并陈述，均为 0.1~0.5d 的小改）

- **静默失败**：文件不存在 `:5364` 直接 return；手动重开同一文件 10s 内被拒 `:5369-5376`（只有 `auto` 才 `NoteAutoOpen`）；`ShowVideo` 的 700ms 闸门 `MainWindow.xaml.cs:99` 会吞掉合法的连续两次不同打开。→ 三处都补一条提示条。
- **时钟源过多**：字幕自有 120ms 定时器 `:3547`+`:4419`，与 50ms 进度定时器、逐帧回调三路并行 → 合并进 render 帧（每 3 帧一次）。
- **隐藏副作用 seek**：`FeedDanmakuToEngine` 在 `pos>15s` 时调 `DshEngine_SeekJump` `:4996` → 播放中刷新弹幕会真 seek 一次（画面顿）。需引擎新增 `AnchorDanmaku(ms)`。
- **短边界**：`_eofHandled` 复位条件 `pos < dur-3000`（`:4861`）对 <3s 短片永不成立 → 回拖到开头不再触发结束。判据改 `pos < Math.Max(0, dur-3000)`。
- **互操作细节**：`int[] cols` 对 `unsigned*`（`:44` vs `dsh_engine.cpp:1948`）、`Seek`/`SeekJump` 重复导出（`:29`/`:74` vs `:2028`）→ 前者改 `uint[]`，后者统一走 `SeekJump`。
- **竞态**：`SwitchQualityAsync` 的 6s 等待循环 `:2928-2932` 无 `_mediaGen` 校验、无取消 → 快速连切时迟到的循环会把位置 seek 到更新那次媒体上（推测，需实测）。
- **布局/虚拟化**：`ControlBar` 52px 塞 13 个控件 `VideoWindow.xaml:61,74-136`，窄窗裁掉右侧；评论 `ListView` 内层禁滚动 `:226,332` → 全量实例化。前者加「⋯」溢出菜单，后者让 ListView 自己滚。
- **设置写入**：每次 Save 全量读改写 JSON `Backend.cs:403-424`；构造时 12 次 `LoadXXX` 各读一次（`:203-233`）；音量/弹幕/倍速三个重复防抖定时器 `:3338`/`:4797`/`:5293` → 进程内缓存 + 单写者 + 一个合并定时器。
- **轮询频率**：AI 字幕状态 800ms `:4049`（设置页打开即常开）→ 空闲退避到 3s。
- **可维护性**：单文件 5700 行混 6 个领域、139 处空 `catch {}`（仅 63 处带日志）→ 分层 + 补日志，见 §3 第 5 条。

## 3. 体验/性能优化方案（按收益排序）

1. **修 P0-1**（0.5d）：一条改动换回白屏事故的唯一出路。
2. **全屏 + 键盘 + 进度记忆**（P1-1/2/3，3.5d）：三者共享同一套 seek/键位基础设施，合做边际成本最低。
3. **`OpenMedia` 去阻塞**（P1-4，1.5d）：冷启动与开片跟手度直接见效。
4. **P/Invoke 能力探测层**（P1-5，1d）：永久消除"DLL 漂移 → 界面说假话"整类问题。
5. **`VideoWindow` 分层**（3d）：按 partial 切七个文件（Player/Timers/Danmaku/Subtitle/Enhance/Chain/Meta），先纯移动+编译验证，再补空 catch 日志。这是后续改动的成本地基。
6. **小改三连**（1.3d）：日志收敛、tab 初始化归位、回到主界面——消除"故障时无信息/无入口"。
7. **窄窗与长列表**（P2，2d）：控制条溢出菜单 + 评论虚拟化。
8. **设置写入统一**（P2，1.5d）：多窗并发不再互相覆盖。

## 4. 推荐实施顺序（Top 10）

| # | 事项 | 级别 | 收益 | 依赖 | 工作量 |
|---|---|---|---|---|---|
| 1 | 重启按钮带 `--video` + 握手判活 | P0 | 白屏可自救 | 无 | 0.5d |
| 2 | 全屏（按钮/F11/双击/Esc） | P1 | 标配能力 | 无 | 1d |
| 3 | 键盘扩展 + 抢焦点修正 | P1 | 标配能力 | #2 共用键位表 | 0.5d |
| 4 | 播放位置记忆/续播 | P1 | 长视频体验 | #5（用同一条 seek 等待） | 2d |
| 5 | `OpenMedia` 去阻塞（字幕异步 + 端口探测异步 + 删重复 ApplyVolume） | P1 | 开片不卡 | 无 | 1.5d |
| 6 | `DshNative.Probe()` 能力位集 | P1 | 消灭假硬件结论 | 无 | 1d |
| 7 | 日志收敛（去 TICK/滑条、轮转 10MB） | P1 | 减 IO、可查 | 无 | 0.5d |
| 8 | `InitGpuDeadWatch` 拆分 + tab 刷新计数独立 | P1 | 看门狗不被拖累 | 无 | 0.5d |
| 9 | 「回到主界面」退化查找 + 置灰 | P1 | 少一个死按钮 | 无 | 0.3d |
| 10 | `VideoWindow` partial 分层 + 空 catch 补日志 | P2 | 后续所有改动的成本 | #1~#9 完成后 | 3d |

合计 ≈ 10.8 人日；#1~#9（7.3d）建议作为"播放器体验第一轮"，#10 作为下一轮的地基。

## 5. 审计覆盖与未覆盖

**已覆盖**
- `VideoWindow.xaml.cs` 6018 行：P/Invoke 桥（23-109）、构造（198-331）、渲染循环与 7 个定时器（3046-3126）、EOS 状态机（4812-4919）、seek 拖拽（4927-4947）、弹幕逐帧（4951-5066）、音量/倍速/独占/HDR/VSR（424-819/3222-3349/5172-5343）、字幕（3542-3610/4405-4520）、`OpenMedia` 与元数据（5361-5800）、画质切换与重启（2742-2936）、增强链节点图（1326-2741，扫读）。
- `VideoWindow.xaml` 976 行（控件树/事件/ControlBar/GpuDeadBanner/6 个 tab）、`MainWindow.xaml.cs` 的 `ShowVideo` 与多窗拉起（91-121）、`Backend.cs` 625 行全文、`App.xaml.cs`、`WinUIGui.csproj`、`deploy.ps1` 全文。
- 引擎侧交叉核对：`dsh_engine.cpp` 的 `Init/OpenMedia/GetPosMs/GetClockPosMs/GetDurationMs/SetDanmaku/GetDanmakuLayout/Seek*/Shutdown` 签名与实现，以及两个 dll 的导出表（二进制字符串扫描）。`ChainWindow.xaml.cs` 仅扫读。

**未覆盖 / 需实测**
- 未运行程序：P1-3 的双触发、P2 里的"迟到的 seek 作用到新媒体"均为静态推断，已就地标注。
- 引擎内部时序（解码线程 `g_mtx` 争用、`Present` 与解码解耦）只读了 C# 调用侧，未审 C++ 解码循环——建议由引擎线补 `06-engine-*.md`。
- `MainWindow` 的卡片拖拽排序、下载页、扩展中心（EPUB/PDF）不在本次范围。
- **`CHANGELOG.md` 止于「补记60 / 2026-10-22 / 发布 1.0.0」，而 csproj 已 1.1.12（git 有 1.1.8~1.1.12 四次提交）→ 4 个版本无开发日志**；该段代码缺日志背书，建议 Lead 在汇总时标注。
- `app/PROJECT_STATUS.md`、`app/未做.md` B 部分、`docs/PROJECT_STATUS.md` 按要求未采信，全部结论据当前源码。
