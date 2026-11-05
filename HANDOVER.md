# 璃鸢镜（Lyra Viewer）项目交接文档

> 交接对象：GPT-6  
> 项目位置：`F:\DSH\LocalMediaPlayer-Workspace`  
> 文档更新时间：2026-10-31  
> 当前主线版本：**璃鸢镜 1.3.6**（已发布）  
> 项目性质：Windows 本地媒体播放器，自研 C++ 音视频引擎 + WinUI 3 前端 + Python 后端 + 网页扩展系统。

---

## 1. 一句话概括

璃鸢镜是一个 Windows 本地/在线媒体播放器：前端是 C# WinUI 3，视频渲染和音频输出在自研 C++ 引擎（Vulkan / D3D11 / WASAPI / ASIO），B 站/YouTube 解析、字幕、AI 字幕、下载等由本地 Python 后端提供；EPUB/PDF 阅读器等附加功能通过独立的网页（WebView2）扩展接入，扩展代码不放进主仓库。

---

## 2. 仓库与发布地址

| 内容 | 地址 |
|---|---|
| 主程序源码 | https://github.com/Lyrueyn/Lyra-Viewer |
| EPUB 阅读器扩展 | https://github.com/Lyrueyn/Lyra-Extension-Epub |
| PDF 阅读器扩展 | https://github.com/Lyrueyn/Lyra-Extention-PDF_Reader （注意仓库名拼写是 Extention，不是 Extension） |

当前最新 Release：

| 项目 | 版本 | 安装/更新资产 |
|---|---|---|
| 主程序 | **v1.4.0** | `LyraViewer-Setup-1.4.0.exe`（安装在用户目录，安装包会安装 .NET 10 Desktop Runtime） |
| EPUB 扩展 | **v0.6.9** | `lyra-ext-epub.zip` |
| PDF 扩展 | **v0.4.7** | `lyra-ext-pdf.zip` |
| TXT 扩展 | **v0.1.2** | `lyra-ext-txt.zip` |
| HTML 阅读器 | **v1.1.0** | `lyra-ext-html-reader.zip` |
| HTML 编辑器层 | **v1.3.1** | `lyra-ext-html-editor.zip` |
| 笔记本 | **v2.3.0** | `lyra-ext-notebook.zip` |
| 番茄小说下载器 | **v0.2.0** | `lyra-ext-tomato.zip`（前端；核心加密随主程序） |

扩展必须和主程序版本配套使用；扩展中心会自动检查 GitHub 最新 Release。

---

## 3. 技术栈与模块边界

```
WinUI 3 前端（C#）
  ├─ 视频窗口 / 视频库 / 下载 / 合集 / 设置
  ├─ 扩展宿主（WebView2，加载 EPUB/PDF 等网页扩展）
  └─ 通过 P/Invoke 调用 dsh_engine.dll

dsh_engine.dll（C++，MinGW 构建）
  ├─ Vulkan 视频渲染 + D3D11 回退
  ├─ WASAPI 独占/共享音频、ASIO
  ├─ FFmpeg 解码、字幕（libass）、HDR/色调映射、FSRCNNX、ICC
  └─ RTX VSR / HDR 的 NVIDIA 桥接

Python 后端（标准库 HTTP 服务）
  ├─ B 站/YouTube 解析、下载、弹幕、评论、字幕
  ├─ AI 字幕（Whisper）、AI 增强（Real-ESRGAN/RIFE/降噪等）
  └─ 本地数据库、媒体库整理
```

关键原则：**引擎是全局单例**。同一进程里不能同时初始化两个视频播放引擎。因此“多视频窗口”开关的实现是 **每个视频启动一个独立进程**（`WinUIGui.exe --video <路径>`），不是同进程多窗口。

---

## 4. 源码目录地图

```
F:\DSH\LocalMediaPlayer-Workspace\
├─ app\
│  ├─ WinUIGui\                 WinUI 3 前端（主要改动区）
│  │  ├─ MainWindow.xaml/.cs    首页、视频库、扩展中心、EPUB/PDF Tab 入口
│  │  ├─ VideoWindow.xaml/.cs   视频播放窗口、弹幕、字幕、设置、RTX 设置
│  │  ├─ ExtensionHostWindow.*  WebView2 扩展宿主（EPUB/PDF 都在这里打开）
│  │  ├─ ExtensionCatalog.cs    扩展发现/启用状态
│  │  ├─ Backend.cs             设置读写、后端 HTTP 客户端
│  │  ├─ UpdateChecker.cs       主程序 GitHub 自动更新
│  │  ├─ EpubReaderWindow.*     早期 EPUB 窗口（现在主要用扩展宿主）
│  │  └─ WinUIGui.csproj        版本号在这里（Version/AssemblyVersion/FileVersion）
│  ├─ dsh_engine\                C++ 引擎源码，产物 dsh_engine_vsr.dll
│  ├─ backend\                   Python 后端（开发用 .venv；安装版用 embedded python）
│  ├─ docs\                      项目说明/开发记录
│  └─ data\                      开发环境的数据（安装版数据在用户目录）
├─ packaging\
│  └─ _pkg_t34\
│     ├─ final\                  安装包最终 payload（前端 + 引擎 + backend + 资源）
│     ├─ runtime\                .NET Desktop Runtime 安装器
│     └─ lmp-setup-user.iss      Inno Setup 安装脚本
├─ third_party\                  RTX VSR 桥接等第三方源码
├─ refs\                         参考资料、截图、测试产物
└─ docs\                         项目文档
```

安装版数据目录（非常重要，**不要随便清空**）：

```
D:\Users\Administrator\AppData\Local\LocalMediaPlayer\
├─ data\
│  ├─ local_media.db             视频库/下载/合集数据库
│  ├─ player_settings.json       播放器设置（音量、RTX、多视频窗等）
│  ├─ epub_library.json          EPUB 书库
│  ├─ pdf_library.json           PDF 书库
│  ├─ epub_library\              添加的 EPUB 文件
│  ├─ pdf_library\               添加的 PDF 文件
│  ├─ extensions_state.json      扩展启用状态
│  ├─ whisper_models\            AI 字幕模型
│  └─ wheels\、pip-cache\        离线依赖 wheel 缓存
├─ extensions\
│  ├─ epub\                      已安装 EPUB 扩展
│  └─ pdf\                       已安装 PDF 扩展
├─ backend\python\               安装版嵌入式 Python 与依赖
├─ Assets\、backend\、ffmpeg DLL 等
└─ WinUIGui.exe
```

---

## 5. 已实现的主要功能

### 5.1 视频播放

- Vulkan 渲染，D3D11 回退（`DSH_D3D11_PRESENT=1` 可切共享音频模式）
- HDR、HDR→SDR（BT.2390 色调映射）、P010 10bit、ICC 校色、去隔行、FSRCNNX 2x
- 字幕：SRT/VTT/ASS（libass）
- 弹幕：B 站弹幕下载、显示、透明度/速度/字号/同屏数量设置
- 音频：WASAPI 独占/共享、48kHz 输出、Hi-Res、ASIO
- 播放列表/视频库/合集、播放进度记忆
- 多视频窗口开关：开启后每个视频独立进程，可真正同时播放

### 5.2 在线视频 / 下载

- B 站、YouTube 解析与下载
- B 站弹幕、评论、CC 字幕
- YouTube 字幕
- 视频库卡片、合集、下载队列

### 5.3 AI 功能

- AI 字幕：Whisper（tiny/base/small/medium/large-v3）
- AI 增强：Real-ESRGAN、RIFE 补帧、DeepFilterNet 降噪、LavaSR 音频超分
- 依赖和模型通过 UI 按钮下载，不要求用户手动 pip
- 注意：AI 依赖装在 `backend\python\Lib\site-packages`；**清空重装安装目录会把这些包删掉**，需要用“安装依赖”按钮重装。模型和 wheel 缓存在 `data\` 下，不会因换安装包丢失。

### 5.4 扩展系统（EPUB / PDF）

扩展是纯网页包：

```
extensions/<id>/
├─ extension.json   { id, name, version, entry, description, fileTypes, enabled }
├─ index.html
└─ lib/...
```

主程序扩展中心：

- 从 GitHub Release 最新版下载 zip 安装/更新
- 启用/禁用/删除
- 已安装扩展在左侧显示独立 Tab（EPUB / PDF），只有安装并启用后才出现

扩展宿主 `ExtensionHostWindow`：

- 用 WebView2 打开 `index.html`
- 打开书时把扩展目录和文档复制到同一临时目录，避免跨域
- EPUB 使用 `?book=book.epub`；PDF 使用 `?file=document.pdf&name=<真实文件名>`
- 加载期间盖一层深色加载帷幕，防止白屏期误点击；扩展发 `lyra-book-ready` 后收起
- 注意：宿主和扩展之间不要重复投喂同一本书，否则会重新解析导致“翻几页回到开头”

### 5.5 EPUB 阅读器（v0.5.26）

- 独立仓库：`Lyrueyn/Lyra-Extension-Epub`
- 目录、章节、字号、主题色、阅读进度
- 窄栏居中排版（约 860px），左右翻页动画为水平平移
- 页面分页使用 CSS 多列 + `scrollLeft`，连续点击用指数缓动，避免一跳一跳
- 打开后前 0.9 秒左右有加载保护，防止初始化重排闪回
- 已修复：宿主重复投递同一本书导致重新打开、点几页后回开头

### 5.6 PDF 阅读器（v0.4.7）

- 独立仓库：`Lyrueyn/Lyra-Extention-PDF_Reader`
- 基于 PDF.js（`lib/pdf.min.js` + worker）
- 连续滚动、页码输入跳转、上一页/下一页
- 左侧页面缩略图栏，点击缩略图平滑滚到对应页；当前页高亮
- 缩放/适应宽度、夜间模式、拖拽打开 PDF
- 页码输入：回车、失焦、停止输入约 0.42 秒都会跳页
- PDF 书库 Tab：打开 PDF（不复制）/ 添加 PDF（复制到书库）/ 卡片阅读

### 5.7 TXT 阅读器（v0.1.2）

- 独立仓库：`Lyrueyn/Lyra-Extension-txt_Reader`
- 左侧栏 TXT Tab：安装并启用 TXT 扩展后显示
- TXT 书库：打开 TXT（不复制）/ 添加 TXT（复制到 `data/txt_library`）/ 卡片阅读
- 宿主注入：`.txt` 复制到临时目录的 `document.txt`，查询参数 `?file=document.txt&name=…&mtime=…`；扩展加载后发 `lyra-book-ready`
- 阅读功能：自动/手动编码、字号行距宽度、深浅主题、位置恢复、键盘/拖放

---

## 6. 构建与打包

### 6.1 前端构建

需要 .NET 10 SDK、Windows App SDK。

```powershell
# 开发构建（输出到干净目录，不要直接用陈旧 bin 打包）
dotnet build app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_build
```

**重要**：项目是 **框架依赖（framework-dependent）** 发布，不是自包含。`bin` 目录里可能残留旧的 `coreclr.dll` / `hostfxr.dll` / `System.Private.CoreLib.dll` 等运行时文件，**打包时必须排除**，否则安装后会报：

```
You must install or update .NET to run this application.
Required: 'Microsoft.NETCore.App', version '10.0.0'
```

正确做法：

1. 用干净输出目录构建（不要用之前跑过 self-contained publish 的 `bin`）
2. 检查输出里没有 `hostfxr.dll`、`coreclr.dll`、`hostpolicy.dll`、`System.Private.CoreLib.dll`
3. 安装包会自带 `runtime\windowsdesktop-runtime-win-x64.exe`，由安装器负责安装 .NET 10 Desktop Runtime
4. 最近一次 1.1.11 安装包就是因为误带运行时文件导致启动报错；**1.1.12 已修复**，以后不要再把 `bin` 全量拷进 final

### 6.2 引擎构建

- 源码：`app\dsh_engine\`
- 当前产物：`app\dsh_engine\dsh_engine_vsr.dll`（导出表需包含 `DshEngine_*` 函数）
- 构建脚本参考：`refs\rtx_vsr_bridge\build_engine.bat`（本地路径，按需改）
- 需要 MinGW g++、Vulkan SDK、FFmpeg 开发库
- 验证导出：`objdump -p dsh_engine.dll | findstr DshEngine_`
- 如果 RTX 设置界面卡在“检测中”，优先检查安装目录的 `dsh_engine.dll` 是否包含 `DshEngine_SetRtxSdkPath` 导出

### 6.3 安装包

安装脚本：

```
F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\lmp-setup-user.iss
```

构建：

```powershell
& "D:\tools\Inno Setup 7\ISCC.exe" "F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\lmp-setup-user.iss"
```

打包前需要准备 `final\`：

- 前端干净输出（排除 .NET 运行时）
- 最新 `dsh_engine.dll`
- `backend\`（嵌入式 Python + 后端代码）
- FFmpeg / 第三方 DLL、Assets
- `runtime\windowsdesktop-runtime-win-x64.exe`

安装包特性：

- `PrivilegesRequired=admin`
- 安装到 `D:\Users\Administrator\AppData\Local\LocalMediaPlayer`（用户目录）
- 先安装 .NET Desktop Runtime，再装主程序
- 不打包扩展；扩展由扩展中心单独下载
- 安装包资产命名：`LyraViewer-Setup-<版本>.exe`

### 6.4 GitHub 发布

- 版本号改 `app\WinUIGui\WinUIGui.csproj` 的 `Version` / `AssemblyVersion` / `FileVersion`
- 提交、打 tag、push
- 用 GitHub API 创建 Release 并上传 Setup 资产
- Token 获取方式：`git credential fill`（不要把 token 打印出来）
- 大文件上传用 `curl.exe -K config`，设置超时 30~60 分钟
- 主程序自动更新逻辑在 `UpdateChecker.cs`：查 `releases/latest`，比较 AssemblyVersion，下载 `Setup` 资产并运行安装器

---

## 7. 当前版本状态（2026-10-31）

| 项目 | 版本 | 状态 |
|---|---|---|
| 主程序 | 1.3.6 | 已发布；扩展接口化（清单动态 Tab/书库）、番茄小说下载器、AI 字幕子进程、PDF/TXT/EPUB 既有功能 |
| EPUB 扩展 | 0.6.9 | 已发布；目录溢出、翻页冷却/串行、跨章首翻、动画缩短 |
| PDF 扩展 | 0.4.7 | 已发布；PPT 式放映、Real-ESRGAN 页面超分、每页独立版本切换、右键菜单禁用、选字/手动缩放 |
| TXT 扩展 | 0.1.2 | 已发布；TXT 侧栏/书库、宿主文件注入、编码与阅读位置、选字开关 |
| 番茄小说下载器 | 0.1.1 | 已发布；免登录匿名设备下载，TXT/EPUB/PDF 按已安装阅读器入库 |
| .NET 运行时 | 10.x | 安装包会安装；本机全局已有 10.0.9 / 10.0.13 |

已知历史问题（已修）：

- 1.1.11 安装包误带 .NET 自包含运行时 → 启动报缺 .NET；1.1.12 修复
- 多视频窗口开关文字黑色 → 改为独立亮色 TextBlock + 自定义 ToggleSwitch 轨道颜色
- 双击视频卡片会开两个独立视频进程 → 700ms 防抖
- PDF 页码输入框只有回车才跳页 → 改为停止输入/失焦/回车都跳
- PDF 无缩略图栏 → 0.2.0 增加
- EPUB 宿主重复投喂导致回开头 → 0.5.22/0.5.26 修复
- RTX VSR 卡“检测中” → 换最新引擎 DLL（含 `DshEngine_SetRtxSdkPath` 导出）

---

## 8. 重要坑与禁止事项

1. **不要清空 `data\`**  
   视频库、书库、AI 模型、wheels 缓存都在里面。重装时只能保留/迁移 `data` 和 `extensions`，不能用破坏性清理。

2. **不要把 .NET 运行时文件打进安装包**  
   见第 6.1。检查 `final` 根目录不能有 `hostfxr.dll`、`coreclr.dll`、`System.Private.CoreLib.dll` 等。

3. **不要在主仓库提交 EPUB/PDF/TXT 扩展源码**  
   扩展是独立仓库，主仓库只保存扩展的下载地址（`AvailableExtensions` 映射）。

4. **不要重复投喂同一本书/PDF**  
   扩展宿主和网页扩展之间已经修过重复解析问题；新增文件类型时也要注意只加载一次。

5. **引擎是单例**  
   同进程只能有一个播放引擎；多视频必须走独立进程，不要试图在同进程创建第二个 `VideoWindow` 同时播放。

6. **单实例锁**  
   主程序有命名 Mutex 单实例；第二次启动会激活已有窗口并退出。`--video` 独立视频进程是例外，用于多视频窗口。

7. **部署 XAML 变更必须带 `.xbf` / `.pri`**  
   只复制 `WinUIGui.dll` 和 exe 时，界面还是旧布局。部署 1.1.12 这类 UI 改动时，要把 `MainWindow.xbf`、`WinUIGui.pri` 等资源一起复制。

8. **AI 依赖目录和模型目录不同**  
   `backend\python\Lib\site-packages` 是包；`data\whisper_models`、`data\wheels` 是模型/缓存。清空安装目录会删包，但不会删模型；需要重新点「安装依赖」。

9. **RTX SDK 本体不入库**  
   `rtx_sdk_path` 存在 `data\player_settings.json`，指向本机 RTX Video SDK 目录；nvngx DLL 由程序复制到程序目录。

10. **发布前先本机启动验证**  
    安装包发布后无法轻易回滚；至少验证：能启动、视频能播、扩展中心和 EPUB/PDF Tab 正常。

---

## 9. 接手后建议的验证清单

1. `dotnet build app\WinUIGui\WinUIGui.csproj -c Debug -o D:\Temp\lyra_check` 能通过
2. 输出目录没有 .NET 运行时文件
3. 启动 `WinUIGui.exe`，确认主窗口正常
4. 视频库能打开一个本地视频，Vulkan/D3D11 正常
5. 扩展中心：
   - EPUB 已安装且启用 → 左侧出现 EPUB Tab
   - PDF 已安装且启用 → 左侧出现 PDF Tab
6. EPUB：打开一本书，连续翻几页再等一会儿，不回到开头
7. PDF：缩略图点击能跳页；页码输入框输入后能跳页
8. 多视频窗口开关：关闭时复用单窗口；开启时打开两个视频应出现两个独立窗口/进程
9. RTX 设置界面不再卡“检测中”
10. 确认主程序版本号与 Release tag 一致

---

## 10. 给 GPT-6 的协作约定

- 用户主要用中文沟通，希望回答**简洁、直接、有证据**，不要绕。
- 报告问题时尽量给出具体文件、日志、版本号、复现步骤。
- 修改前先读现有实现，不要凭猜测大改。
- 不要做破坏性清理（尤其 `data`、`extensions`、用户书库/视频库）。
- 长任务不要因为上下文长就缩减范围；该读的文件要读完。
- 工作区路径：`F:\DSH\LocalMediaPlayer-Workspace`；主程序安装路径：`D:\Users\Administrator\AppData\Local\LocalMediaPlayer`。
- 构建/发布命令、GitHub token 获取方式、安装包路径都在本文档第 6 节。

---

## 11. 参考文件

- 主 README：`README.md`
- 工作区说明：`WORKSPACE.md`
- 开发记录：`app\CHANGELOG.md`（若有）
- 安装脚本：`packaging\_pkg_t34\lmp-setup-user.iss`
- 前端设置读写：`app\WinUIGui\Backend.cs`
- 扩展宿主：`app\WinUIGui\ExtensionHostWindow.xaml.cs`
- 主窗口（视频库/扩展中心/EPUB/PDF Tab）：`app\WinUIGui\MainWindow.xaml.cs`
- 视频窗口（播放/设置/RTX/弹幕）：`app\WinUIGui\VideoWindow.xaml.cs`
- EPUB 扩展：`https://github.com/Lyrueyn/Lyra-Extension-Epub`
- PDF 扩展：`https://github.com/Lyrueyn/Lyra-Extention-PDF_Reader`
- TXT 扩展：`https://github.com/Lyrueyn/Lyra-Extension-txt_Reader`

## 扩展分层架构（主程序 1.4.0+）

```text
HTML_Reader（reader，独立 Tab）
└─ HTML_Editor（layer，无 Tab，iframe 嵌入）
   └─ Notebook（notebook，独立 Tab，多笔记壳）
```

### 主程序改动

- `ExtensionCatalog.cs`
  - 新增 `ExtensionKind.Layer`（`kind:"layer"`）。
  - `ExtensionInfo.Requires` 运行时前置列表。
  - `MissingRequires(def)`：打开阅读器/笔记本前校验前置。
- `ExtensionHostWindow.xaml.cs`
  - 固定共享虚拟域名 `lyra-extensions.local` 映射整个 `extensions/` 根目录。
  - 打开扩展时 URL 增加 `extensionsOrigin`、`extensionId`。
  - 父扩展可 iframe：`https://lyra-extensions.local/html-editor/index.html?embedded=1`。
- `ExtensionTabView.xaml.cs`
  - 打开前检查 `requires`，缺少时给出提示。
- `MainWindow.xaml.cs`
  - 命令行 `WinUIGui.exe xxx.html` 自动寻找 reader 扩展打开。
- `WinUIGui.csproj`：1.4.0。

### 扩展仓

| 扩展 | 版本 | 仓库 | 资产 |
| --- | --- | --- | --- |
| HTML_Reader | 1.0.0 | https://github.com/Lyrueyn/Lyra-Extension-HTML-Reader | `lyra-ext-html-reader.zip` |
| HTML_Editor | 1.0.0 | https://github.com/Lyrueyn/Lyra-Extension-HTML-Editor | `lyra-ext-html-editor.zip` |
| Notebook | 2.0.0 | https://github.com/Lyrueyn/Lyra-Extension-Notebook | `lyra-ext-notebook.zip` |

- HTML_Editor：由旧 notebook 1.14.0 的 UI 抽成 `embedded=1` 模式；隐藏自身标题栏，通过 `lyra-editor` 通道交换 HTML/assets。
- HTML_Reader：`kind: reader`，`requires:["html-editor"]`；阅读/编辑切换，`host.workspace.load/save` 写回原 `.html`。
- Notebook 2.0：`kind: notebook`，`requires:["html-reader","html-editor"]`；壳负责 `.lyrnb` 包、笔记列表、assets 与自动保存；主区域 iframe 嵌入编辑器。
- 旧 Notebook 1.14.0 源码备份：`D:\Temp\Lyra-Extension-Notebook-1.14-src`（git tag v1.14.0 也保留）。
- `.lyrnb` v2 格式未变；v1 JSON 笔记读取时转换为 HTML。

### 本机验证

- 主程序 1.4.0 安装到 `%LOCALAPPDATA%\LocalMediaPlayer`。
- 层扩展安装：`extensions/html-editor`、`extensions/html-reader`、`extensions/notebook` 2.0.0。
- Notebook：用测试 `.lyrnb` 启动，壳 + 编辑器 iframe 正常显示三栏。
- HTML_Reader：命令行打开测试 HTML，阅读模式正常；点击“编辑”后编辑器层加载并显示源码/预览/DevTool；修改源码后保存，原文件 mtime 与内容变化。
- Playwright 验证 Notebook assets 桥：`.lyrnb` 中 `assets/tiny.png` 被传给编辑器，预览 `img` 解码为 data URL（1×1）。
- Playwright 验证 Notebook 保存：编辑后 mock `host.workspace.save` 收到包数据。

### 已知限制

- HTML_Reader 读取本地 HTML 时，`<img src="相对路径">` 相对路径解析仍以扩展页为基准，尚未映射原文件目录；data URL / 绝对 URL 正常。
- layer 扩展会被扩展中心列出（安装前置），但没有独立 Tab。
- Notebook 2.0 顶部壳较简化；旧 1.14 的完整 DevTool 功能保留在 HTML_Editor 层内。

### 1.0.1 / 2.1.0 追加

- HTML_Editor 1.0.1：Format 先按块级标签断行；多行属性标签 +2、style 声明再 +2、`>` 回标签列；`<style>` 内 CSS 自动换行缩进；标签按 doc/style/script/block/text/media 分色。
- Notebook 2.1.0：笔记自动补全完整 HTML 头（DOCTYPE/html/head/meta/title/style/body）；旧片段加载时包装；顶部编辑/阅读切换；阅读 iframe 把 assets 转 data URL。
- 本机安装：主程序 1.4.0、HTML_Editor 1.0.1、HTML_Reader 1.0.0、Notebook 2.1.0。
- 发布：HTML_Editor v1.0.1、Notebook v2.1.0；HTML_Reader 仍 v1.0.0。
- 验证：Playwright 检查 Format 行缩进、标签颜色 class、Notebook 阅读 iframe、full HTML 默认文档；实机 Notebook 源码已显示完整头。

### 1.1.0 / 2.2.0 追加

- HTML_Editor 1.1.0：预览缩放 50–150%；inline style 颜色属性系统取色器，取色直接写回源码。
- Notebook 2.2.0：阅读字号 A−/A+、浅色/护眼/深色主题、窄/中/宽宽度；设置写入 `.lyrnb`；笔记上移/下移/复制。
- Playwright 验证：阅读设置 16→18px、护眼主题、680px 宽度生效；复制笔记生成第二篇；保存包。

### 1.2.0 / 2.3.0 / 1.1.0

- HTML_Editor 1.2.0：CSS 标签页新增 Style blocks，右栏直接编辑 `<style>` 内容并写回源码；新增 Wrap 开关处理超长 data URI。
- Notebook 2.3.0：阅读模式不再注入字号/主题/宽度/body 样式，完整 HTML 原样渲染；移除阅读调参控件。
- HTML_Reader 1.1.0：阅读模式同样原样渲染，不再注入 padding/字体/背景。
- 实测：Notebook 阅读 iframe 中页面自己的 body `background:#123456; color:#abcdef; font-size:30px; width:600px` 全部保留；Reader 同样保留。

### Editor 1.3.0

- 打开文档 / 切换笔记 / Reader 进入编辑模式时自动格式化一次，解决旧笔记 source 中的 `<img>` 属性缩进凌乱问题。
- 自动格式化 silent：不标记 dirty；用户编辑/保存才写回。

### Editor 1.3.1

- 增加 normalizeOpeningTags 兜底后处理：多行属性强制缩进标签 +2、删除开标签内空行、`>` 回标签列；不再依赖 beautify 的 wrap 输出。
- 自动格式化/Format 都走该后处理；用于修复 `<img` 后空行、src 顶格、alt 缩进不齐。
