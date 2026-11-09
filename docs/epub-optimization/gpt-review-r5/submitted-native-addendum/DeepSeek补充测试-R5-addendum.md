# R5 supplement addendum：R4 0.8/v2 扩展 → R5 最终宿主

本文件只追加；不修改已冻结的 47 份 `r5-evidence-supplement` 文件，也不改 R5 产品/候选/证据。

## 1. 互配对象与身份

- 新隔离目录：`D:\Temp\lyra_r5_supp_addendum_v2interop`
- 宿主：R5 最终 `WinUIGui.dll=202954E5930FCDC7A50BD96975B84A12CBF7AA4375AC7334832D28A45F5F9480`，exe `D0F1…B335`
- 扩展：冻结 R4 0.8.0 ZIP，完整干净解包，SHA `B128B9DFBE9D7A1085CC3505F9B3EDC645847CFA24DA33958EB26EDD685F184B`；`extension.json=0.8.0`，reader `659A466F…DACE4`，reader-state `A790F06F…FCC0`
- PID `43440`；identity `v2interop-identity.json`。

## 2. 原生观察

- 打开 long.epub 后，宿主日志原样记录 v2：
  - `version:2 type=app.ready`
  - `version:2 type=hello requestId=hello-c75b…`
  - `version:2 type=book.ready loadId=4acf…`
- **宿主回复数 = 0**（日志中无任何 `REPLY`）；R5 v3 处理器不承接 v2 状态，也未隐藏 R5 加载遮罩。
- 原生加载遮罩：
  - 3 秒 `01-3s.png`：中央仍是 spinner + “正在打开…”。
  - 9 秒 `02-9s.png`：仍 “正在打开…”。
  - 12 秒 `03-12s.png`：仍覆盖；点击正文中心 + ArrowRight 后 `04-after-right.png` 仍只有遮罩，没有可见正文，无法证明正文可交互。
  - 22 秒 `05-22s.png`：宿主超时态 “仍在加载这本书…” + “重试/取消”，仍然遮罩，未显示 local 正文/footer。
- `book.ready` 日志存在，但宿主未回应、未进入 ready 生命周期；因此不能把这次互配写成“local 提示可见、正文可交互”。**实际是：v2 hello 超时/协议不承接，宿主 loading 遮罩不消失，正文被遮罩挡住，local 提示无法从原生界面观察。**
- `v2interop-host.log` 是完整原始日志；截图是真实 WinUI 窗口。

## 3. 冻结补充报告的两处口径更正

### A08 组合前后位置
补充报告把 combined 用例写成 page3/textOffset3072，这是与另一条 **text-anchor** 用例混写。实际：
- text-anchor 单独用例：textOffset3072/page3（`a08/text-anchor-*`）。
- combined 用例关窗前后/整体重启的可见文字为 **textOffset1758/pageIndex2**（截图第3/14页、paragraph12）；3072 不应归入 combined。
- 两条用例的真实 state/screenshot 都在 `native/a08/`；此处只更正文字，不覆盖冻结文件。

### A19 第二窗 ack:false 的原因
GPT 复核原日志显示第二窗 `host.finalized ok:false` 前是 **STALE_INTERACTION**（旧位置资格被另一窗新位置取代）。这不是关闭保存逻辑丢弃新位置，也不应通过重新授权覆盖另一窗的新位置来“修”。补充报告只记录 ack:false 与两书签仍保留；现补充原因：旧资格按设计被拒绝，不能因此改业务。

## 4. 结论

- R4 v2 扩展 + R5 v3 宿主：**生命周期不被 R5 宿主承接；原生遮罩不消失，正文不可见/不可交互；local 提示无法在原生窗口中观察到**；不冒充 local 可用。
- 未改 R5 产品/候选/已冻结 47 文件；本 addendum 及其清单单独散列。
