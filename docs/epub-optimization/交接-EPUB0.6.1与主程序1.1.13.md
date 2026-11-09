# 交接：EPUB 扩展 0.6.1 + 主程序 1.1.13（2026-10-30，工作已暂停）

用户反馈的三个问题：
1. 扩展中心「检查更新」一直出不来结果，但直接点「更新」能正常更新；
2. 切章时没有翻页那种动画，读起来不连贯；
3. 保存位置没用，关掉窗口再打开又是第一页。

要求：修好后发新版本。

当前进度：三个问题都找到原因并改好了代码。扩展已提交并推送标签，但 **GitHub Release 还没建**。主程序改动 **还没提交、没改版本号、没打安装包**。

---

## 1. 原因与修法

### 1.1 检查更新出不来（主程序问题）

- 原因：主程序用 GitHub 接口 `api.github.com/repos/<仓库>/releases/latest` 查最新版本。这个接口不登录时按 IP 限每小时 60 次。实测本机已经用完：返回 403，`X-RateLimit-Remaining=0/60`。代码把错误吞掉了，`_extLatest` 一直是空的，界面就一直显示「正在检查更新…」。
- 「更新」按钮能用的原因：版本号为空时按钮照样显示，下载走的是 `github.com/.../releases/latest/download/...`，这条路不受接口限流。
- 修法（`MainWindow.xaml.cs`）：新增 `FetchLatestTagAsync`。先请求网页地址 `https://github.com/<仓库>/releases/latest`，并且不自动跟随跳转，从 302 跳转的 `Location` 里取 `/tag/vX.Y.Z`。这条路不受限流，实测返回 302，指向 `tag/v0.6.0`。取不到时再用接口兜底。两条都失败就显示「检查更新失败（网络或 GitHub 暂时不可用），点刷新重试」，用 `_extLatestFailed` 记录。
- 主程序自身更新（`UpdateChecker.cs`）用同样的办法：先看跳转结果，版本不比当前新就直接返回。如果接口被限流，但跳转已确认有新版本，就按约定的附件名拼下载地址：`releases/download/<tag>/LyraViewer-Setup-<版本>.exe`。已核对 v1.1.12 的附件确实叫 `LyraViewer-Setup-1.1.12.exe`。

### 1.2 关窗再开回第一页（主程序问题）

- 原因：用户装的主程序 1.1.12 就是 git HEAD 版本，宿主里没有 `lyra-reader` 保存接口，扩展只能退回网页本地存储（localStorage）。但宿主每开一个窗口都会生成一个随机虚拟域名 `lyra-extension-xxxxxxxx.local`，而本地存储是按域名分开存的，所以下次开窗就找不到上次的记录了。
- 证据：已装程序的 WebView2 本地存储文件（`WinUIGui.exe.WebView2\EBWebView\Default\Local Storage\leveldb\000003.log`）里有 **75 个不同的 `lyra-extension-*` 域名**。
- 修法（`ExtensionHostWindow.xaml.cs`）：
  - 虚拟域名改为按扩展 id 固定，用 `StableHostFor(id)` 生成 `lyra-ext-epub.local`。
  - 同时把 R8 已写好、但一直没进 git 的 `ReaderStateStore.cs` / `ReaderProtocol.cs` 一起带上。位置改为宿主写盘，存到 `<安装目录>\data\reader\epub\books\<书的sha256>.json`。
- 注意：用户以前存的进度在那 75 个随机域名下，迁移不过来，升级后第一次打开每本书仍从头开始，之后就正常了。**只更新扩展、不更新主程序，这个问题还在**，所以主程序必须发 1.1.13。

### 1.3 切章没有动画（扩展问题）

- 原因：`navigateToSpine` 切章时马上盖上「正在切章…」遮罩，而且没有任何过渡效果。章内翻页走的是 `paginator.goToPage` 的淡出 90ms / 淡入 130ms / 14px 轻移。
- 修法（`reader.js`，提交 667f8e7）：
  - 原 `navigateToSpine` 改名为 `navigateToSpineCore`，外面包一层：先淡出 90ms，再切章，切完淡入 130ms，参数和翻页一致。动画用 Web Animations（浏览器自带的动画接口）作用在 iframe 元素上。
  - 动画只在翻页翻过章节边界（`fromTurn`）和点「上一章 / 下一章」按钮（`fxDir`）时生效。目录、书签、搜索、开书恢复都不加动画。
  - 打开「减少动画」时不播动画。
  - 新增 `showLoadingDelayed`：切章超过 450ms 还没好才显示遮罩，不再每次闪一下。
  - 切章淡入还没结束就接着翻页时，用 `cancelChapterFx()` 让翻页动画接管。

---

## 2. 已验证

| 项目 | 方法 | 结果 |
|---|---|---|
| 扩展回归 | `tests/reader-smoke.cjs` 12 项、`tests/reader-extra.cjs` 9 项（真实 Edge） | 全部 PASS |
| 切章动画 | `D:\Temp\lyra_r8_ui_probe\probe-chapter-fx.cjs`（Edge 逐帧采样） | 翻页进下一章、翻页回上一章、点「下一章」：透明度最低 0.15、位移 14px，约 104ms 换章、约 230ms 复原，无遮罩闪现，无残留动画；减少动画模式下直接切换 |
| 主程序编译 | `dotnet build -c Debug -o D:\Temp\lyra_host_1113_dbg` | 0 错误；DLL SHA-256 `FA3E304E…FDE54` |
| 原生：关窗再开 | 隔离目录 `D:\Temp\lyra_1113_test`，新主程序 + 0.6.0/0.6.1 扩展，UIA + 物理点击 | 翻到第 4/9 页，关窗回执 `host.finalized ok=true revision=3`，盘上 `data\reader\epub\books\9d35f0f7…json` 写入 textOffset=1654，重开后状态栏为「第 1 / 2 章 · 本章 4 / 9 页」✔ |
| 原生：检查更新 | 同上，扩展页 | 显示「已是最新版本 v0.6.0」✔（此时 GitHub 接口仍处于限流状态） |
| 原生：切章 | 同上，第 9/9 页再点下一页 | 正确进入第 2/2 章 ✔；截图间隔太粗，没抓到中间帧，动画以 Edge 逐帧数据为准 |

**尚未在原生环境里实测**：主程序自身的更新路径，即 `UpdateChecker` 在被限流时走跳转兜底下载安装包这条路。

---

## 3. 坑（接手前必看）

1. **测试主程序必须用 Debug 构建，或按 HANDOVER.md 的打包流程来。** 用 `-c Release` 直接 build 出来的 runtimeconfig 带有 `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false`（csproj 里非 Debug 配置开了 `PublishTrimmed`，也就是裁剪）。结果 `ExtensionCatalog` 用反射解析 `extension.json` 时静默失败，扩展列表为空，EPUB 标签消失，界面显示「尚未安装扩展」。另外 Release 产物的 runtimeconfig 写的是 `includedFrameworks`，放进干净目录会弹「You must install .NET」。正式打包前务必确认：final 目录里的 runtimeconfig 用的是 `framework` 节点，而且没有那条反射开关。
2. 同一本书点两次卡片会开出两个阅读器窗口：我的 `native_drive.py open-book` 点了两下。这是测试脚本的问题，不是产品问题。
3. 双击、键盘输入进不了 WebView2，必须先 `SetForegroundWindow` + `AttachThreadInput` 激活窗口，再用物理鼠标点击。截图要用 `PrintWindow(hwnd, …, 2)`，不能用屏幕坐标截图。工具在 `D:\Temp\lyra_r8_ui_probe\native_drive.py`，需要 Python 3.14 + comtypes：`D:\Users\Administrator\AppData\Local\Programs\Python\Python314\python.exe`。
4. 在 pwsh 里取 GitHub 令牌，必须用 `cmd /c "git credential fill < 文件"`，直接用管道传会报 missing protocol field。不要打印令牌。

---

## 4. 当前状态

### 扩展仓库 `D:\Temp\Lyra-Extension-Epub`（分支 r8）
- 提交 `667f8e7`：「release: 0.6.1 …」。`extension.json` 版本 0.6.1，`index.html` 的 `?v=` 全部改为 0.6.1，README 标题 0.6.1。
- 已推送 `origin/r8`，标签 `v0.6.1` 也已推送。
- 已打包：`D:\Temp\lyra_release_061\lyra-ext-epub.zip`，105,147 字节，SHA-256 `EAE851058793CB047383639237AFC255DB8BC37FE0611FFD1C83D882F3C1DF29`，manifest 版本 0.6.1，11 个文件，不含 tests。
- **未做**：GitHub Release v0.6.1 还没建，附件也没上传。GitHub 上目前的 latest 仍是 v0.6.0。

### 主程序仓库 `F:\DSH\LocalMediaPlayer-Workspace`（main，HEAD b978661）
- 未提交的改动：`ExtensionHostWindow.xaml(.cs)`（R8 宿主协议 + 固定域名）、`MainWindow.xaml.cs`（检查更新）、`UpdateChecker.cs`（主程序更新兜底），以及 git 里还没有的 `ReaderProtocol.cs`、`ReaderStateStore.cs`。
- 工作区里还有用户自己的大量其他未提交文件（.md、.bat、.glsl 等），**提交时只 add 上面这 6 个文件**，别用 `git add -A`。
- **未做**：版本号还是 1.1.12（csproj 的 `Version` / `AssemblyVersion` / `FileVersion`，以及 `packaging\_pkg_t34\lmp-setup-user.iss` 的 `MyAppVersion` 和 `OutputDir`）。

### 测试环境
- `D:\Temp\lyra_1113_test`：隔离运行目录，Assets、third_party 是指向安装目录的目录链接（junction）。**删除前要先 `rmdir` 掉这两个链接**，否则会把安装目录里的东西一起删掉。
- 所有测试进程都已关闭，当前没有任何 WinUIGui 进程在运行。用户的安装目录和数据都没动过。

---

## 5. 接手后的步骤

1. **发扩展 0.6.1**：用 GitHub API 建 Release `v0.6.1`，名称「EPUB 阅读器 0.6.1」，设为 latest，上传 `D:\Temp\lyra_release_061\lyra-ext-epub.zip`。发完用 `releases/latest` 跳转和下载地址各核对一次散列。
2. **主程序 1.1.13**：
   1. 把 csproj 的三个版本号改成 1.1.13 / 1.1.13.0。
   2. 按 HANDOVER.md 第 6 节打包：干净目录构建，排除 .NET 运行时文件，把 `.xbf` 和 `.pri` 一起放进 final。
   3. 检查 final 里的 runtimeconfig（见第 3 节第 1 条）。
   4. 改 iss 的版本号和输出目录，用 ISCC 出 `LyraViewer-Setup-1.1.13.exe`。
   5. 先在本机干净目录启动验证：EPUB 标签在、开书、翻页、关窗再开回到原页、扩展页显示版本号。
   6. 只提交第 4 节列出的 6 个文件，打标签 `v1.1.13`，推送后建 Release 并上传安装包。
3. 更新 `HANDOVER.md` 第 7 节的版本表：主程序 1.1.13，EPUB 0.6.1。
4. 告诉用户：
   - 要同时更新主程序和扩展，进度保存才会生效。
   - 以前的进度存在 75 个随机域名下，迁移不过来，每本书第一次打开仍从头开始。
