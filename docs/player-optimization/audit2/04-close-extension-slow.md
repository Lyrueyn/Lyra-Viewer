# 04 · 关闭扩展阅读窗口耗时较长（EPUB/PDF 扩展宿主）

- 工作区：`F:\DSH\LocalMediaPlayer-Workspace`，分支 `opt/player-top3`，HEAD `30f6d3c`
- 被查对象：1.1.13 + Top3 构建。已核对 `D:\Users\Administrator\AppData\Local\LocalMediaPlayer\WinUIGui.dll` SHA256 = `1C9B8B45…D6009`，与 `D:\Temp\lyra_host_top3_build\WinUIGui.dll` 完全一致；二进制内含 `host.finalize` / `host.finalized` 字符串，确认跑的就是本仓库源码。
- 扩展版本：epub `0.6.1`、pdf `0.3.0`（`extensions\<id>\extension.json`）。
- 只读调查，未改源码/安装目录/临时目录。

## 0. 一句话结论

**关闭不是"销毁窗口"，而是"取消这次关闭 → 等扩展回 finalize 回执 → 再第二次 Close"。** 窗口在等待期间一直停在屏幕上，因此全部等待都变成肉眼可见的卡顿。PDF 扩展（0.3.0）**根本没有实现 `host.finalize`**，所以每次关 PDF 窗口**恒定吃满 1500 ms 超时**；EPUB 实测回执仅 1–9 ms，属正常，只有在有未提交位置/设置或 `finalizing` 已置位时才会顶到 1500 ms。次要问题：`Closed` 里同步递归删临时目录，且删除静默失败，已泄漏 108 个目录 / 295 MB。

## 1. 关闭路径梳理（调用链 + 行号）

`app/WinUIGui/ExtensionHostWindow.xaml.cs`：

1. 用户点 X → `AppWindow.Closing` → `OnAppWindowClosing`（L562）。
2. L564 `if (_closeReady) return;`；L565 `if (_closeStarted) { args.Cancel = true; return; }`
3. L566–568 置 `_closeStarted=true`、`_finalizePending=true`、生成 `_finalizeRequestId`。
4. **L570 `args.Cancel = true;`** ← 真正的关闭被取消，窗口继续可见。
5. L571–576 `PostWebMessageAsJson({"type":"host.finalize", requestId})`。
6. **L578 `StartTimer(1500ms, () => FinishFinalizeAndClose("timeout", …))`** ← 硬性 1500 ms 关闭期限。
7. 回执入口 `OnWebMessageReceived`（L287）：
   - L294 `IsCurrentSource(source)` 先过滤（不匹配则回执被丢弃 → 只能等超时）；
   - L296–311 解析 `host.finalized`；
   - L306–308 校验 `_finalizePending` + `requestId` + `sessionId` → `FinishFinalizeAndClose("ack"/"error")`。
8. `FinishFinalizeAndClose`（L581–590）：停表 → `_closeReady=true` → **L589 `Close()`**。
9. `Closed`（**L88** `Closed += (_, _) => CleanupTemp();`）→ `CleanupTemp`（L653–661）→ **L658 `Directory.Delete(_tempDir, true)`**，同步跑在 UI 线程。
10. `MainWindow.xaml.cs` L745 `win.Closed += (_, _) => _extHosts.Remove(win);`（这步很快）。

时序（以 1500 ms 超时路径为例）：

```
点X(0ms) --Cancel--> 窗口仍在屏上 --finalize--> 等回执 …… 1500ms --> Close() --> Closed --> Directory.Delete --> 窗口消失
```

## 2. 根因（按证据强度排序）

### R1【确凿】PDF 扩展无 `host.finalize` 实现 → 每次关闭恒定 1500 ms

`extensions\pdf\reader.js` 全文只有 1 处 webview 调用：`window.chrome?.webview?.postMessage({ type: "lyra-book-ready" })`。**没有 message 监听、没有 `host.finalize` 分支**。宿主 L578 无条件启动 1500 ms 定时器，回执永不到达 → 必然走 `FinishFinalizeAndClose("timeout")`。用户感知即为"关 PDF 扩展页总要等一秒半"。

### R2【确凿】关闭语义是"取消原关闭再二次 Close"（L570 + L589）

`args.Cancel = true` 使首次关闭被吞掉，窗口可见期 = finalize 等待期。**任何回执延迟 1:1 变成肉眼卡顿**，这是慢感的结构性成因，与 R1 叠加。

### R3【确凿】EPUB `finishAndAck` 最坏 4300 ms，被宿主 1500 ms 截断

`extensions\epub\reader.js` L1813–1886：
- L1850–1858 `bounded(flushPrefs(), 2800)` + `bounded(flushSave(), 2800)` + `bounded(pendingMetadata, 2800)`；
- L1860–1863 `settleFailedSavesForClose()` 再 `Promise.race` 1200 ms；
- L1822–1825 `restoreLiveView` 300 ms。
合计上限 300+2800+1200 ≈ 4300 ms。命中失败重试时会顶满宿主 1500 ms。

另 **L1814 `if (state.finalizing) return;` 提前返回且不发回执** → 必等满 1500 ms（重复 finalize / 状态残留时）。

**但要注意（正面结论）**：`docs\epub-optimization\r8-native\logs\final-host-debug.log` 实测 `CLOSING → CLOSE_ACK` 为 **1–8 ms**：

```
[21:45:03.988] CLOSING …  [21:45:03.989] CLOSE_ACK …   → 1 ms
[21:54:13.066] CLOSING …  [21:54:13.074] CLOSE_ACK …   → 8 ms
[21:56:50.469] CLOSING …  [21:56:50.471] CLOSE_ACK …   → 2 ms
```

`r4-evidence\native-pending-prefs-close.log`、`r5-evidence\native\r5-native-pending-close.log` 同为 1–9 ms。**所以 EPUB 稳态关闭不慢；慢的是 PDF（R1）和极少数 finalize 卡死场景（R3）。**

### R4【确凿】`Closed` 内同步递归删临时目录，且静默失败导致泄漏

L88 → L658 `Directory.Delete(_tempDir, true)` 在 UI 线程同步执行。临时目录由 L128–137 创建：`CopyDir(_extensionDir, _tempDir)` + 复制书。**PDF 扩展目录 193 文件 / 3.29 MB**（实测拷贝 105 ms、删除 43 ms），EPUB 11 文件 / 0.35 MB。

由于 L139 `SetVirtualHostNameToFolderMapping(_host, folder, …)` 把该目录交给 WebView2，关闭时 WebView2 尚未释放，`Directory.Delete` 多数抛异常 → L658 `catch { }` 吞掉、L659 `_tempDir = null` → **永不重试**。实测泄漏证据：

- `D:\Temp\lyra_ext_host`：**108 个残留目录 / 3614 文件 / 295.03 MB**，最早 2026-10-25，最新 2026-10-30 20:30（1.1.13 实际运行后仍在漏）。
- 对比 `WinUIGui.exe.WebView2` 用户数据目录 446 文件 / 76.29 MB（无清理代码，非主因）。

### R5【推断，需实测】WebView2 全程无显式释放，销毁成本落在 UI 线程

`ExtensionHostWindow.xaml.cs` 与 `MainWindow.xaml.cs` 全文 grep **无 `Dispose`、无 `CoreWebView2.Close()`、无 `Unloaded` 处理**。`CoreWebView2` 仅 L96 `EnsureCoreWebView2Async()`。故控件销毁/浏览器进程回收依赖 XAML 卸载与终结器，耗时未测量（带 pdf.js canvas+worker 的 PDF 页通常 100–300 ms）。日志末尾 L… 可佐证 teardown 期仍有事件派发：

```
[21:56:50.471] CLOSE_ACK …
[21:56:50.486] ACTIVATED active=False epoch=2   ← Close() 后 15 ms 仍在派发 Activated → PostHostState()
```

### R6【确凿】生产环境零可观测性，慢关闭无法归因

`LogHost`（L592–598）在 `_debugLog`（`LYRA_READER_DEBUG != "1"`）时 **直接 return**。安装目录只有 `dm_debug.log`（6.19 MB，`HH:mm:ss.mmm`），grep `扩展宿主|ExtensionHost|CLOSING|CLOSE_` **零命中**；`reader_host_debug.log` 不存在。因此用户现场的关闭耗时目前完全不可测。

### 已排除的怀疑点

- **同步等待扩展（`Task.Wait`/`GetAwaiter().GetResult()`）**：全文无此类调用，关闭是"定时器 + 回执"异步模型。
- **关闭时同步 flush 到后端 HTTP**：`ReaderStateStore.cs` 为纯本地文件实现（L155–165 `File.WriteAllText` + `fs.Flush(true)` + `File.Replace`，L299 `catch (IOException) { Thread.Sleep(10); }` 重试），**不经过 `Backend`/HttpClient**，无 15 分钟长超时问题。
- **主窗解冻/重绘同步等待**：`MainWindow` 仅 L745/876/1099 注册 `Closed` 移除引用，无 `EnableWindow` 阻塞。

## 3. 修复方案（代码级改法 + 验收 + 工作量）

### F0【首选，性价比最高】先隐藏窗口，再后台收尾

`OnAppWindowClosing`（L562）在 `args.Cancel = true`（L570）之后**立即隐藏窗口**，finalize 与 1500 ms 期限原样保留在后台：

```csharp
args.Cancel = true;
try { AppWindow.Hide(); } catch { }          // ← 用户视角已"关掉"
```

窗口隐藏后 R1/R3 的等待不再可见，且**不改变任何数据安全性**（finalize 照常跑完、照常落盘）。`FinishFinalizeAndClose`（L589）的 `Close()` 随后真正销毁窗口。
验收：点 X 到窗口消失 <100 ms；位置/设置落盘与现状一致（对照 `state.savePosition` 回执 revision）。
工作量：**0.5 h**。

### F1 给 PDF 扩展补 finalize 应答（消除 R1 的 1500 ms 源头）

`extensions/pdf/reader.js` 增加监听（PDF 侧不使用 `state.*` 协议，无待冲刷数据，可同步应答）：

```js
window.chrome?.webview?.addEventListener("message", (ev) => {
  const d = ev && ev.data;
  if (!d || typeof d !== "object" || d.type !== "host.finalize") return;
  window.chrome.webview.postMessage({
    type: "host.finalized", requestId: d.requestId || "",
    sessionId: "", ok: true, revision: 0, closeDiagnostics: [],
  });
});
```

工作量：**0.5 h**。

### F2 宿主超时 1500 ms → 250 ms，并对旧扩展走快路径（R1/R3）

`ExtensionHostWindow.xaml.cs` L578：

```csharp
_closeTimer = StartTimer(TimeSpan.FromMilliseconds(HasReaderProtocol ? 250 : 0), …);
```

新增字段 `_protocolSeen`，在 `OnWebMessageReceived` 识别到 `protocol == "lyra-reader"` 的 v3 消息时置 true。未见过协议的扩展（PDF 0.3.0 等）直接 `FinishFinalizeAndClose("legacy", "", 0)`，不等待。
依据：实测回执 1–9 ms，250 ms 已是观测 p100 的 25 倍以上。
工作量：**0.5 h**。

### F3 临时目录清理移出关闭路径（R4）

删掉 L88 的同步清理，改为在 `FinishFinalizeAndClose` 内、`Close()` 之后异步带重试：

```csharp
var temp = _tempDir; _tempDir = null;
try { Close(); } catch { }
if (temp != null) Task.Run(() => {
    for (int i = 0; i < 10; i++) { try { Directory.Delete(temp, true); return; } catch { Thread.Sleep(50); } }
});
```

另在应用启动时清扫 `Path.GetTempPath()\lyra_ext_host\*`（一次性回收现有 295 MB）。
验收：连开/关 10 次后 `lyra_ext_host` 残留 0 个目录。
工作量：**1 h**（含启动清扫）。

### F4【可选，同时加快打开】把"扩展目录"和"书目录"拆成两个虚拟主机

现在 L128–139 把扩展目录**复制**到临时目录再映射，导致打开 105 ms 拷贝 + 关闭 43 ms 删除。改为 `SetVirtualHostNameToFolderMapping` 映射两次：扩展目录用 `_host`、只含书文件的临时目录用 `_bookHost`，临时目录从 193 文件降到 1 文件，删除近乎免费。
代价：需扩展接受绝对书地址参数（PDF/EPUB 各改一处）。
工作量：**3–4 h**。

### F5 显式释放 WebView2（R5）

在 `FinishFinalizeAndClose` 内 `Close()` 之后补：

```csharp
try { Wv.CoreWebView2?.Stop(); } catch { }
try { Wv.Dispose(); } catch { }        // 或 AppWindow 隐藏后再 DispatcherQueue.TryEnqueueLowest
```

工作量：**0.5 h**（含一轮回归，确认无 teardown 期 `PostHostState` 异常刷屏）。

### F6 打开 `LogHost` 的耗时埋点（R6）

把 `LogHost` 改为**始终**记录 `CLOSING` / `CLOSE_ACK` / `CLOSE_TIMEOUT` / `CLEANUP_MS`（仅在 `LYRA_READER_DEBUG=1` 时记录 raw 消息体），无参数时也写轻量行。
工作量：**0.5 h**。

**总工作量**：快赢组合 **F0+F1+F2 ≈ 1.5 h**；完整方案 **F0–F6 ≈ 6–7 h**。

**验收标准**
1. 点关闭到窗口消失 <300 ms（目标；F0 后应 <100 ms），EPUB 与 PDF 各 10 次取 P95。
2. EPUB 位置/设置零丢失：关闭后重开，`spineIndex/pageIndex/chapterFraction` 与关闭前一致（对照 `state.savePosition` 回执）。
3. `D:\Temp\lyra_ext_host` 在连开/关 10 次后残留目录数为 0。
4. 不出现"点 X 无反应"：`_closeStarted` 分支（L565）与 `LoadingCancel_Click`（L650）路径回归通过。

## 4. 风险与回滚

- **缩短超时的风险**：若某次 finalize 真在落盘（R3 的 2800 ms 分支），250 ms 会提前销毁窗口 → 位置丢失。F0 先隐藏、后台跑完 finalize 可**完全规避**该风险，故 F0 应优先于 F2 落地；F2 只对"从未发过 v3 协议消息"的扩展生效（PDF 无状态可丢，安全）。
- **`Wv.Dispose()` 风险**：可能在 teardown 期触发 `PostHostState`（L276）对已释放对象调用。已有 `catch { }` 包裹（L267–278），但需回归确认 `ACTIVATED active=False` 那条路径不抛未捕获异常。
- **两个虚拟主机（F4）风险**：改变扩展加载来源，可能影响 localStorage 同源隔离（1.1.13 刚为此修过"固定虚拟域名"，见 L28–30 注释）。**建议 F4 单独立项，不与本次快赢同批发布。**
- **回滚**：全部改动集中在 `ExtensionHostWindow.xaml.cs`（F0/F2/F3/F5/F6）与 `extensions/pdf/reader.js`（F1），均为独立小 diff，可按文件回滚；F1 独立于宿主，可单独回退而不影响其他项。

## 5. 未证实项

1. **WebView2 teardown 实际耗时**：无 `Dispose` 埋点，未测。需 F6 落地后在真机测 `Close()`→进程消失的间隔。
2. **`IsCurrentSource`（L294）是否会丢弃 PDF 的 finalize 回执**：PDF 无回执，无法区分"没发"与"被过滤"，需 F6 记录 raw 后确认。
3. **`state.finalizing`（epub L1814）早退导致无回执**的实际发生率：日志中未见 `CLOSE_IGNORED`/超时样本，属代码路径推断。
4. **主窗（`MainWindow`）在扩展窗口关闭时是否被冻结**：本次在 `MainWindow.xaml.cs` 未找到 `EnableWindow`/冻结调用，与任务描述中的"主窗冻结/解冻回调"不符，可能已在其他提交移除或位于别的文件，未进一步展开。
5. **用户现场的真实关闭耗时**：无任何埋点（R6），无法量化；上述 1500 ms 为代码确定的**上限**，非实测值。建议先做 F6 再定优化幅度。
6. 任务描述称 `ExtensionHostWindow.xaml.cs` "现约 1300+ 行"，实测 HEAD `30f6d3c` 为 **670 行**；本报告以实际文件为准。
