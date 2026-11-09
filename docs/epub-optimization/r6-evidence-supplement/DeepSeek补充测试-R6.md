# DeepSeek R6 补充测试

只写新 `r6-evidence-supplement` 和新隔离目录；未改产品/候选/原 r6-evidence，未发布/安装。

## 1. 身份

- 产品：`r6` / `99bd102f4c9b4c1a92668d04e2e12260e523ad94`，候选 0.10.0
  `F2B2741A166749E7108E010151A46246AC2DF40BF1F0861050E8E12D612C535C`
- 最终宿主：`D:\Temp\lyra_r6_supplement`（从最终候选复制）
  DLL `3F30CCC9915993F058DAD62879F1619AC1EBD5B735CE1741D09FAEE86B28719C`
  reader `5A81D526B8126B590738BC50652DB829D701FDB9E3319240766FB3D07C2FC837`
- R6 evidence SHA256SUMS 原值 `1E8D60EAF12661F3DE45DE46F059D3F573701317B2FA9AC1D317DFA69408A3A6`，未改。

## 2. A08 最终 R6 正常重开/重启（原生）

- 新隔离数据根 `D:\Temp\lyra_r6_supplement\data`，A08 有效合成书（有效 PNG、文字 c0/图片 c1/文字 c2）。
- PID 见 `native/a08/a08-identity.json`。步骤：
  - 打开 A08，文字页向右两次到 page3，真实加书签（host `bookmark.add` 2 次扫描命中，state bookmarks=1），行距设为 2.25；
  - 关窗，`CLOSE_ACK`；完全退出 WinUI 再启动，重新打开；
  - 重启后 state `positionRevision=3, metadataRevision=1`，position 为 textOffset1758/pageIndex2（截图第3/14页 paragraph12），bookmarks=1，preferences lineHeight=2.25。
- 证据：`native/a08/01-before-close.png`、`02-after-restart.png`、`close.log`、`after-restart.log`、`a08-identity.json`、`prefs.json`、book state。
- 结论：A08 正常稳定路径 R6 复测 PASS；未覆盖异常关闭位置（A08 剩余要求仍不扩大）。

## 3. A11：ready 后图片几何变化

- 真实 Edge，新合成书含 `<img>` 与长正文；在 reader iframe 安装 `HTMLImageElement.complete` 显式门控（不改业务代码，公开注入点），使 `_waitForResources` 在图片未完成时超时进入 ready；ready 后再放开图片并改 480×480 几何，触发原迟到图片 layout 路径。
- 结果 `a11/a11-results.json`：t0→ready 2,809ms；before `ready=true, busy=false, pageCount=18, nodes=123, imgComplete=false`；释放后 `imgComplete=true`，600ms 后仍 `ready=true, busy=false, pageCount=18, nodes=123`；静置 1s 状态不变，无空白/回零。
- **未完成**：字体加载/字体几何变化没有构造；本证据只覆盖 ready 后图片晚完成与重排，A11 仍 PARTIAL（不复制旧 PASS）。

## 4. A23 原生与浏览器

### 原生尝试（失败，原样记录）
- 新书 A23-NATIVE-201（201 含图章），隔离运行；尝试用真实 WinUI 点击“下一章”按钮 200 次并采样 WinUIGui + WebView2 进程 `WorkingSet64/HandleCount`。
- 结果 `native/a23/native-200-identity.json`：requested=200，distinctSuccess=0，lastSpine=-1。按钮坐标自动点击未能改变章节；没有把 0 成功冒充 200。失败步骤=用固定窗口相对坐标 x=0.960,y=0.033/0.037 点击工具栏“下一章”，截图仍在 c0；未继续猜测坐标以免产生无效证据。
- 进程趋势只在 idle/点击失败期间采样（`samples`：GUI ~176-180MB、WebView2 ~816-917MB），不能代表 200 真实切章负载，不作为 A23 通过证据。

### 浏览器 200 成功切章 + 10 同上下文换书（已有 R6 证据）
- `r6-evidence/a23/results.json`：201 含图书，200/200 步断言 spine 目标+ready 全成功；同页真实 `#file` 入口连续换 10 书；资源 1→0，taskSets 1，静置 heap 有界；耗时 2,378ms。
- 该证据是真实 Edge/localStorage，不是原生进程 RSS；A23 仍 PARTIAL。

## 5. A21/PDF 矩阵（最终 R6 宿主）

- c12fe26 扩展 + R6 最终宿主，有书打开成功：`compat/r6-c12-withbook-identity.json`，PID30076，扩展0.5.26，index `88FA…9721A`。
- 旧 b978661 宿主 + 干净 R6 0.10.0 包，有书打开：`compat/r6-oldhost-r6pkg-identity.json`，PID30780，DLL `57CC…AAF3CD`，WebView `1.1.12+b978661…`，reader `5A81…C837`。
- R3 0.7.0 客户端 + R6 宿主，有书打开：`compat/r6-host-r3client-identity.json`，PID26888，扩展0.7.0。
- PDF0.3：有书打开（PID12492，窗口 PDF 0.3）；无书从扩展中心第二入口打开（PID29860，PDF 窗口1）。截图/identity 在 compat/。
- **未完成**：c12/旧宿主“无书”入口、R3 无书/关闭回执、PDF 多窗/关闭未逐项；A21 仍 PARTIAL。

## 6. 未完成与说明

- A11 字体重排未构造；A23 原生 200 自动点击失败；两者不写 PASS。
- A/B 失败账本与操作身份原 JS→原 C# 按 GPT 要求未重复，交由独立专项。
- 其他 A 子项没有用 R5 PASS 代替 R6；本轮只新增上述证据。
- 未改 R6 产品/候选/原证据；所有新文件在 supplement，另有 manifest/SHA256SUMS。
