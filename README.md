# 璃鸢镜（Lyra Viewer）v2.0.13

> 🤖 **本项目由 AI（DeepSeek Harness）制作/协作开发。**
>
> 一个面向 Windows 的本地/在线媒体播放器：
> **WinUI 3 前端 + 自研 C++ 音视频引擎 + 本地 Python 后端 + WebView2 阅读扩展**。
> 支持 B 站 / YouTube 下载、弹幕、评论、字幕、AI 字幕（Whisper）、RTX VSR、HDR、音频增强，以及 EPUB / PDF 阅读。

## 下载安装

最新版本：**璃鸢镜 2.0.13**

| 内容 | 地址 |
|---|---|
| 主程序 Release | https://github.com/Lyrueyn/Lyra-Viewer/releases/latest |
| 安装包 | `LyraViewer-Setup-2.0.13.exe` |
| EPUB 扩展 | https://github.com/Lyrueyn/Lyra-Extension-Epub |
| PDF 扩展 | https://github.com/Lyrueyn/Lyra-Extention-PDF_Reader |
| TXT 扩展 | https://github.com/Lyrueyn/Lyra-Extension-txt_Reader |
| HTML 阅读器扩展 | https://github.com/Lyrueyn/Lyra-Extension-HTML-Reader |
| HTML 编辑器层 | https://github.com/Lyrueyn/Lyra-Extension-HTML-Editor |

- 安装到当前用户目录，不需要管理员权限，不写 Program Files / ProgramData。
- 安装包会安装 .NET 10 Desktop Runtime；需要 Windows 10/11 x64。
- EPUB / PDF 扩展由扩展中心自动检查 GitHub Release 更新；主程序更新也会跟随 Release。

## 免责声明与下载器说明

番茄小说下载器扩展仅供网络爬虫技术、网页数据处理及相关研究的学习用途。签名、匿名设备注册、内容密钥和接口地址等核心实现不包含在公开源码中，只以编译加密后的组件随主程序安装包发布。下载内容请自行阅读后删除，禁止转载、传播或商业使用；使用者自行承担相关责任。详见 `DISCLAIMER.md`。

## 功能

### 本地播放

- Vulkan 视频渲染，D3D11 回退；HDR / HDR→SDR（ITU-R BT.2390）色调映射。
- FSRCNNX 2× 超分、去隔行（bwdif/yadif）、ICC 校色、字幕链路（SRT/VTT/ASS）。
- WASAPI 独占 / 共享输出、Hi-Res、ASIO；音频采样率在打开设备前正确设置。
- 多视频窗口、播放列表、播放完成后的结束判定与回调（播放位置记忆不实现）。

### 在线视频与下载

- B 站 / YouTube 解析与下载；B 站弹幕、评论、合集、播放列表。
- 视频库、收藏、合集、下载任务管理；B 站 Cookie / YouTube 登录态管理。
- 支持 Hi-Res 音频优选、分 P、播放列表批量下载。

### 字幕与增强

- B 站 CC 字幕、YouTube 字幕抓取；可手动导入本地字幕。
- **AI 字幕**：本地 Whisper，可选 tiny/base/small/medium/large-v3，按模型单独管理，繁→简统一，GPU/CPU 自动选择。
- **画质增强**：RTX VSR、Real-ESRGAN、RIFE 补帧、DeepFilterNet 降噪、LavaSR 音频超分；支持增强链和版本管理。
- 1.1.14 起：切画质使用 `DshEngine_OpenMediaAt(path, startMs)`，不再先闪片头帧；VSR 生命周期移出 Present，降低 TDR/未响应风险；NVNGX 探测按进程缓存。

### EPUB / PDF 阅读扩展

- **EPUB 0.7.0**：正文区域 Ctrl+滚轮调整字号；目录长标题换行、侧栏不横向溢出、正文序号修复；翻页冷却与串行化、跨章过渡、快速翻页不积压；1/3 动画冷却、页内/跨章动画协调。
- **PDF 0.4.7**：PPT 式单页放映（左右点击翻页、留白、退出恢复缩放、F5；放映/全屏按整页适配屏幕重新出图）；工具栏可直接输入 25%–500% 缩放；可切换正文是否允许选中；Real-ESRGAN 页面超分（整篇/范围、2×/4×）；每页超分完成后才出现独立版本选择（原图/已生成倍率），切换不卡顿；禁用 WebView2 默认右键菜单。
- PDF 超分需要主程序 **1.1.15+** 的 `/api/pdf/upscale` 后端接口；主程序会自动把实际后端端口传给扩展，避免多实例端口顺延。

### TXT 阅读扩展

- **TXT 0.1.2**：左侧栏 TXT Tab、TXT 书库（打开/添加/卡片/管理删除）、自动编码（UTF-8/BOM、GB18030、UTF-16）、字号/行距/内容宽度、深浅主题、阅读位置恢复。
- 主程序支持 `.txt` 宿主注入：复制到临时目录后通过 `?file=document.txt&name=…&mtime=…` 传入，扩展自动加载并发 `lyra-book-ready`。
- 扩展中心可安装/更新 TXT 扩展；书库保存在 `data/txt_library.json` / `data/txt_library`。

### 笔记本扩展

- `.lyrnb` 格式 v2：每篇笔记是独立 `notes/<id>.html`，支持任意 HTML/CSS
- 三栏编辑器：CodeMirror 源码高亮/补全、中实时预览、右 95 项英文 CSS 调参；支持热重载、元素检查、CSS 规则/伪状态、DOM 搜索折叠、Assets 替换/引用检查、响应式预览、DOM 重命名/删除、源码光标联动、全量 CSS 属性补全、格式化排版、属性面板整合、软换行/即时同步
- 基础富文本：标题、粗体/斜体/下划线/删除线、列表、引用、代码块、分割线
- 自动保存，宿主原子写回并保留 `.bak`
- 图片、表格、PDF 导出等后续版本继续加

### 扩展系统

- 扩展中心安装/启用/更新网页扩展（EPUB、PDF、TXT）。
- 扩展宿主为每个窗口使用独立虚拟域名，支持 EPUB/PDF 与宿主之间的阅读位置、书签、偏好持久化协议。
- 关闭扩展窗口有 finalize 握手和超时兜底；旧版扩展不会拖满固定 1500ms。
- 三个阅读书库都支持封面：卡片显示封面；每个 Tab 有独立的「改封面」开关，默认隐藏封面按钮；无自定义封面或删除后自动生成第一页封面（PDF 渲染第 1 页、EPUB 取封面图/第一段正文、TXT 取开头正文）。

## 版本状态

| 项目 | 版本 | 状态 |
|---|---|---|
| 主程序 | 2.0.13 | 已发布 |
| EPUB 扩展 | 0.7.0 | 已发布 |
| PDF 扩展 | 0.4.7 | 已发布 |
| TXT 扩展 | 0.1.2 | 已发布 |
| 番茄小说下载器 | 0.2.0 | 已发布（前端开源，核心加密随主程序） |
| HTML 阅读器 | 2.0.13 | 已发布 |
| HTML 编辑器层 | 1.19.9 | 已发布 |
| 笔记本 | 2.3.37 | 已发布 |

扩展分层（1.4.0+）：HTML_Reader（阅读器 Tab）→ HTML_Editor（无 Tab 的编辑器层）→ Notebook（多笔记容器 Tab）。
| Python 后端 | 0.1.0 | 随主程序打包 |

近期主线变更：

- 1.3.3：新增笔记本扩展；1.0.0 起改为 HTML 三栏编辑器。
- 1.3.2：番茄下载器恢复发布，但逆向核心改为 .pyc 编译 + AES 加密，随主程序安装包发布；扩展仓库只保留前端和免责声明，不包含签名/注册/接口实现。
- 1.3.1：番茄下载器封面/真实作者修复；书籍更新（续更合并导出）与旧书库重名清理；下载元数据 book.json 交给阅读器书库。
- 1.3.0：扩展架构改为清单驱动：新增读者扩展只需 extension.json + 网页，主程序自动生成侧栏 Tab/书库，不再改 MainWindow；新增番茄小说下载器 0.1.1（免登录、按已安装阅读器导出 TXT/EPUB/PDF 并自动入库）。
- 1.2.2：AI 字幕改由一次性子进程转写，完成后子进程退出，不再让常驻后端持有 PyTorch 模型、CUDA 上下文和显存；识别进度继续实时更新。
- 1.2.1：AI 字幕识别进度改为按 Whisper 内部帧进度实时更新；加载模型/识别阶段有明确提示，不再长时间停在 0%。
- 1.2.0：视频卡片 +/× 仅悬停显示；三扩展书库支持管理删除文件；PDF/TXT 可切换选中文字；PDF 缩放可手动输入；PDF 放映清晰度不再受普通缩放影响。
- 1.1.17：三扩展书库封面；自定义封面 / 删除后回退第一页；每 Tab 独立「管理」开关。
- 1.1.16：TXT 阅读器侧栏/书库、`.txt` 宿主注入与 `lyra-book-ready` 生命周期。
- 1.1.15：PDF 超分后端接口、扩展宿主传递后端端口、禁用扩展默认右键菜单。
- 1.1.14：第二轮现场问题修复（OpenMediaAt、切画质遮罩、VSR 后台初始化、NVNGX 缓存、扩展关闭优化、ABI 3）。
- 1.1.13：宿主阅读位置存储、固定扩展域名、检查更新兜底。

## 目录结构

```text
app/
  WinUIGui/               WinUI 3 前端（C#）
  dsh_engine/             自研 C++ 音视频引擎（Vulkan / D3D11 / WASAPI / ASIO）
  backend/                Python 后端（本地 HTTP 服务：解析/下载/字幕/AI/增强/PDF 超分）
  docs/                   使用手册与方案文档
  data/                   运行时数据（书库、PDF、配置、数据库）
packaging/
  _pkg_t34/               打包工作目录
    lmp-setup-user.iss    Inno Setup 安装脚本（当前使用）
docs/
  player-optimization/    播放器优化与第二轮现场问题报告/证据
third_party/
  rtx_vsr_bridge/         RTX VSR 的 C++ 桥接源码（SDK 本体不入库）
refs/                     本地构建参考与工具（不随安装包分发）
```

## 构建 / 运行

### 1. 后端

```bat
py -3.10 -m venv app\backend\.venv
app\backend\.venv\Scripts\pip install -r app\backend\requirements.txt
app\backend\.venv\Scripts\python app\backend\run_backend.py --port 8765
```

需要 `yt-dlp`、`ffmpeg` / `ffprobe`（可用 `FFMPEG_PATH` 指定，或放到 PATH / `third_party\ffmpeg`）。
AI 字幕/增强/Real-ESRGAN 的模型与依赖不随仓库提供，在播放器设置里按需下载。

### 2. 前端

需要 Windows 10/11、.NET 10 SDK、Windows App SDK：

```bat
dotnet build app\WinUIGui\WinUIGui.csproj -c Release
```

### 3. 引擎

需要 Visual Studio C++ 工具链、Vulkan SDK、FFmpeg 开发库和 MinGW（当前使用 MinGW g++ 构建 `dsh_engine.dll`）。
构建脚本见 `refs/rtx_vsr_bridge/build_engine.bat`（本地开发用路径，按自己的依赖位置修改）。

### 4. 安装包

先把主程序、依赖、引擎和运行库布置到 `packaging\_pkg_t34\final`，再执行：

```bat
"F:\DSH\LocalMediaPlayer-Workspace\refs\_tools\InnoSetup\ISCC.exe" ^
  "F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_t34\lmp-setup-user.iss"
```

产物在 `packaging\_pkg_t34\inno-out-131\璃鸢镜-Setup-1.3.1.exe`。

## 说明 / 许可

- 本仓库只包含项目源码与自有资源；**不包含**第三方 SDK 二进制、FFmpeg / Qt / Vulkan 运行库、Whisper / Real-ESRGAN 模型、下载的视频等大文件。
- **NVIDIA RTX Video SDK** 为 NVIDIA 专有许可，SDK 头文件/库/运行 DLL 不入库；使用 RTX VSR 功能需自行取得 SDK 并遵守其许可。
- 第三方组件遵循各自许可（FFmpeg LGPL/GPL、Qt LGPL/GPL、Whisper MIT 等）。
- 本仓库根目录的 `LICENSE` 为 GNU GPL v3。

版本：**1.1.17**


## 2.1.0 编辑器扩展架构

- 编辑器本身是扩展（`kind=editor`），提供左侧「编辑器」Tab；Tab 只记录项目路径，不复制文件。
- 点击编辑器项目库里的项目 → 在独立窗口打开；该窗口复用 HTML 阅读器窗口，HTML 编辑器插件存在时直接进入编辑模式。
- HTML_Editor 改为 `kind=editor-plugin`，由编辑器扩展在独立窗口中加载；UI 与之前完全一致，也可被 HTML_Reader 嵌入。
- 没有编辑器扩展/HTML 编辑器插件时，HTML_Reader 只能阅读，隐藏编辑/保存按钮。
- HTML_Reader 支持 `startEdit=1`，从编辑器项目库打开时直接进入编辑模式；阅读/编辑模式可正常切换。
- 扩展安装顺序：HTML 阅读器 → 编辑器 → HTML 编辑器插件。

### 2.1.0 安装包

```
F:\DSH\LocalMediaPlayer-Workspace\packaging\_pkg_210\inno-out-210\璃鸢镜-Setup-2.1.0.exe
433,990,115 bytes
SHA-256: 41EA0EFE744CFBC08790FFE1D816D9D5D28F7C996B81D83C9332DBC46498066E
```

### 扩展包

```
编辑器 1.1.0       6,359 bytes   C932C6B0AB528BA16EC1C4A4CDC536ED635597DB5E5F768D935EC539BB157EA1
HTML编辑器 1.20.0  234,751 bytes 089ACBC8BA2222AEE1237B4E223D9CAF3A75AFBF7665F27A725C96AC40AC8019
HTML阅读器 2.1.1   29,960 bytes   4C6857ECBD8D72D599126326B191246F23A7273D92774C21B6A51506D6B5C374
笔记本 2.3.38      55,177 bytes   50B4A0E330E31487133620E3E550A0326146887E7944B9EE7D6E3FC795A19366
```


## HTML 编辑器 1.21.0：Dev Tool 增强

- Inspect hover 浮层：标签选择器、宽高、Color、Font、Padding、Margin、Background、Border。
- 浮层底部显示 ACCESSIBILITY：Name、Role、Keyboard-focusable，并增加文本对比度（AA/AAA）。
- Dev Tool Styles 面板列出所有匹配的 CSS 规则，逐条标记「生效 / 覆盖」。
  - 内联 style 与 `<style>` 规则一起参与级联。
  - 简写属性会展开到 longhand 做覆盖判断，减少误标。
- 新增 Computed、Box model、Accessibility 三个面板。
- 颜色值前面显示色块。

### 扩展版本

```
HTML 编辑器 1.21.0   242,428 bytes  DD4FD8E82F4DA485893EDBC10D670337CC26272DA714E6C4DA3BE78E8AF171CA
HTML 阅读器 2.1.2    29,967 bytes   7E464AD39F7C7C4E4CB7A45188A0245589329A8750E289C02A548008CEC30434
笔记本 2.3.39        55,179 bytes   E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
编辑器扩展 1.1.1     6,375 bytes    DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
```

主程序仍为 2.1.0，安装包无需更新。


## 项目文件树：新建 / 重命名 / 删除（HTML 阅读器 2.1.3 + 主程序 2.1.1）

- 文件树顶部新增 `＋`（新建文件）和 `▤＋`（新建文件夹）按钮。
- 右键文件/文件夹/空白处弹出菜单：
  - 新建文件
  - 新建文件夹
  - 重命名
  - 删除
- 新建文件默认补 `.html` 后缀并写入基础模板；新建文件夹即使为空也会显示。
- 重命名支持文件/文件夹；如果当前打开的文件在改名范围内，会自动切到新路径。
- 删除需要确认；当前打开文件被删除时自动切到下一个 HTML 文件。
- 主程序宿主新增 `host.file.mkdir`，并把 `host.file.save/delete/rename` 在项目模式下扩展为可操作项目根目录内任意文件/文件夹。

### 版本

```
主程序 2.1.1          433,986,289 bytes  2F6936A88D409CF0C6544E918E8274B3FE08B3C4C3A17E5DC92DC61F922E1522
HTML 阅读器 2.1.3     33,095 bytes       F85A0C93B37CD264B85388303F131572864D1F45C2A8ED353545C54B12FFA707
HTML 编辑器 1.21.0    242,428 bytes      DD4FD8E82F4DA485893EDBC10D670337CC26272DA714E6C4DA3BE78E8AF171CA
笔记本 2.3.39         55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
编辑器扩展 1.1.1      6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
```


## JavaScript 编辑器插件 1.1.0

编辑器插件按文件类型使用不同 UI：

- 编辑器扩展项目库：带 `package.json` 的 JS 项目优先打开 JavaScript 编辑器；HTML 项目打开 HTML 编辑器。项目卡片右键可强制选择插件。
- JS 插件文件树：
  - `.js/.mjs/.cjs/.json` 在 JS 编辑器内打开
  - `.html/.htm` 自动交给 HTML 编辑器插件打开
  - `.css` 等待后续 CSS 编辑器插件
- JS 专属 UI：文件 / 代码 / Outline 三栏，Console + REPL 输入，运行文件 / 运行选中；不再使用 HTML 的 DevTool 或通用 Preview。
- Outline 自动提取函数、类、箭头函数、顶层变量，点击跳转。
- 运行代码在浏览器沙箱 iframe 中执行，console 输出回 Console。

### 版本

```
主程序 2.1.2            433,957,343 bytes  3963EEE07A4D29A06EE888A6F16823605E09F5E3BCB357F924345E183DC8EBCF
JavaScript 编辑器 1.1.0  93,357 bytes       9F14BFA26030072C809F7E32C408385EC6C3ED8F1D1836EF8E602ED02EEB7F8C
HTML 阅读器 2.1.3        33,095 bytes       F85A0C93B37CD264B85388303F131572864D1F45C2A8ED353545C54B12FFA707
HTML 编辑器 1.21.0       242,428 bytes      DD4FD8E82F4DA485893EDBC10D670337CC26272DA714E6C4DA3BE78E8AF171CA
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## JavaScript 编辑器 1.2.0 / HTML 编辑器 1.22.0

### JavaScript 编辑器 1.2.0

- 代码折叠：折叠 gutter、折叠全部/展开全部、快捷键。
- 查找替换：Ctrl+F，大小写、上一个/下一个、替换、全部替换。
- 撤销/重做：按钮 + Ctrl+Shift+Z / Ctrl+Y。
- 自动保存：停止输入约 0.9 秒，状态栏显示自动保存时间。
- 运行：完整文件路径、运行中状态、✓/✕ 结束标记、停止按钮、两次运行空行分隔。
- 界面风格：JavaScript / 经典蓝 / 深空紫 / 森林绿 / 暖阳橙 / 樱花粉 / 极简白。

### HTML 编辑器 1.22.0

- 源码编辑器补全代码折叠（brace fold helper + 快捷键）。

### 版本

```
主程序 2.1.2            433,957,343 bytes  3963EEE07A4D29A06EE888A6F16823605E09F5E3BCB357F924345E183DC8EBCF
JavaScript 编辑器 1.2.0  103,203 bytes      FE66E5701FBD234322A51A045695D7169480A848586EAA154097570DA6342EE4
HTML 编辑器 1.22.0       243,986 bytes      71F7F547FB5A67708FCDD7303566839AA19E8B36232C491A33F3AE33FB0B27C1
HTML 阅读器 2.1.3        33,095 bytes       F85A0C93B37CD264B85388303F131572864D1F45C2A8ED353545C54B12FFA707
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 编辑器单窗口外壳（2.1.4 / Editor Shell 1.0.0）

- 编辑器扩展项目库打开一个**编辑器窗口外壳**，左侧固定项目文件树，右侧按文件类型内嵌插件：
  - HTML 文件 → 内嵌 HTML 编辑器（HTML 阅读器 + HTML 编辑器插件）
  - JS / JSON 文件 → 内嵌 JavaScript 编辑器
  - 切换文件类型不再新开窗口，始终只有一个编辑器窗口。
- 外壳模式下 HTML 编辑器自己的「项目」按钮和项目侧栏隐藏；修正项目侧栏占位造成的空白，编辑区铺满。
- 外壳窗口依赖：主程序 2.1.4+、编辑器扩展 1.1.1+、Editor Shell 1.0.0+、HTML 阅读器 2.1.6+、HTML 编辑器 1.22.0+、JavaScript 编辑器 1.2.1+。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
编辑器外壳 1.0.0         7,025 bytes        2EFEEEBB8A7C976AAB4E01279C1DB858641B6C94DC56899800177711EB593FAA
JavaScript 编辑器 1.2.1  103,548 bytes      BC40223C3E188DEF572FCEF5DA1F11D754E131E82EF1E98344E8A7F4A2A2860E
HTML 阅读器 2.1.6        34,111 bytes       6E2DF8225B37E776925F20D4FD511E4E67966480571D966B3730460BBF0AAAE9
HTML 编辑器 1.22.0       243,986 bytes      71F7F547FB5A67708FCDD7303566839AA19E8B36232C491A33F3AE33FB0B27C1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 编辑器外壳主题（Editor Shell 1.1.0）

- 外壳新增 6 套界面风格：经典蓝、深空紫、森林绿、暖阳橙、樱花粉、极简白。
- 风格会记忆，并同步给内嵌的 HTML 编辑器 / JavaScript 编辑器插件。
- 外壳模式下插件隐藏重复的品牌/项目名/主题下拉，避免顶栏重复。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
编辑器外壳 1.1.0         8,212 bytes        C6089BD8A2C8EE13439809B9EA9065D34711A3284D305ABEC22D4872EE2924CA
JavaScript 编辑器 1.2.2  103,643 bytes      F7F8B038DCA5C8DD25AA6B989DE41FA2410EAF2943AF59265E523CFC52C181A1
HTML 阅读器 2.1.7        34,184 bytes       7B435F8672F2DC28F4BF208FEC4B973D438C7B210FB3C387B9E7D72A31B25CD7
HTML 编辑器 1.22.0       243,986 bytes      71F7F547FB5A67708FCDD7303566839AA19E8B36232C491A33F3AE33FB0B27C1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## DOM 树 hover 高亮 + 主题（HTML 编辑器 1.23.0 / Reader 2.1.8）

- DOM 树 hover 一个节点时，预览区用蓝框高亮对应元素；点击节点仍然选中并跳源码。
- DOM 树颜色跟随编辑器主题；浅色主题使用低饱和 hover/active 与高对比语法色，避免整块过亮。
- 外壳主题会一路同步到内嵌 HTML 编辑器（外壳 → Reader → HTML 编辑器）。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
HTML 编辑器 1.23.0       244,353 bytes      E222DDB6F739EC01C0FC5B8BE0FD8BFDB7DF87244309253ED3E77CF212A59AB8
HTML 阅读器 2.1.8        34,256 bytes       B4B9B2516EB4EDC5044585301CD8FCED6606012ACB33BE8C0A782449749B444F
编辑器外壳 1.1.0         8,212 bytes        C6089BD8A2C8EE13439809B9EA9065D34711A3284D305ABEC22D4872EE2924CA
JavaScript 编辑器 1.2.2  103,643 bytes      F7F8B038DCA5C8DD25AA6B989DE41FA2410EAF2943AF59265E523CFC52C181A1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 主题闪烁 / 首开卡顿修复（HTML 编辑器 2.6.2 / Reader 2.1.9）

- 根因：外壳主题 → Reader → HTML 编辑器，HTML 编辑器又把自己的主题回传给 Reader，形成来回 postMessage 的主题回环，表现为原/目标主题快速闪烁，并伴随首次打开卡顿。
- 修复：
  - 外壳主题只向下传递；HTML 编辑器接收父级主题时不再回传。
  - Reader 在外壳模式下忽略 HTML 编辑器发来的主题，避免夺回主题控制权。
  - 主题相同直接跳过，减少重复样式重算。
  - 嵌入模式隐藏重复主题下拉。
  - 开始编辑模式时跳过隐藏阅读 iframe 的重复渲染，降低首开占用。
- 验证：Playwright 记录一次外壳主题切换，Reader / HTML 编辑器主题各只变化一次（violet），不再在 violet/lyra 间来回跳。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
HTML 编辑器 2.6.2        65,834,573 bytes   F788BE9744CA1BD04A5DDA20BA5A3BA6B4FF91F4BBA1927A4F8891D0462EF9DB
HTML 阅读器 2.4.0        38,351 bytes       E41C830CC382D35033518FD264DD08FC6F9130700501B5C1A6EAEB1A2AD2FF66
编辑器外壳 1.1.0         8,212 bytes        C6089BD8A2C8EE13439809B9EA9065D34711A3284D305ABEC22D4872EE2924CA
JavaScript 编辑器 1.2.2  103,643 bytes      F7F8B038DCA5C8DD25AA6B989DE41FA2410EAF2943AF59265E523CFC52C181A1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 编辑器外壳文件树操作（Editor Shell 1.2.0）

- 文件树最顶层固定显示项目根目录。
- 右键项目根目录 / 文件夹：新建文件、新建文件夹；文件夹额外支持重命名、删除。
- 右键文件：重命名、删除。
- 新建/重命名/删除后自动刷新文件树；当前打开文件被改名/删除时自动切到新路径或关闭。
- 项目根目录本身不提供重命名/删除，避免把整个项目目录改坏；根目录只提供新建。
- 文件操作走主程序 `host.file.save / mkdir / rename / delete`。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
编辑器外壳 1.2.0         11,501 bytes       C2E484F4057B79CBF69FD7786A8F8883984269918E4226F9614539743B1AB5B8
HTML 阅读器 2.4.0        38,351 bytes       E41C830CC382D35033518FD264DD08FC6F9130700501B5C1A6EAEB1A2AD2FF66
HTML 编辑器 2.6.2        65,834,573 bytes   F788BE9744CA1BD04A5DDA20BA5A3BA6B4FF91F4BBA1927A4F8891D0462EF9DB
JavaScript 编辑器 1.2.2  103,643 bytes      F7F8B038DCA5C8DD25AA6B989DE41FA2410EAF2943AF59265E523CFC52C181A1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 文件树对齐 + 文件夹折叠三角（Editor Shell 1.2.1）

- 文件夹行增加小三角，显示展开/折叠状态。
- 文件行保留隐藏占位三角，同层级文件夹和文件的图标/文件名对齐。
- 版本：编辑器外壳 1.2.2（11,596 bytes，SHA-256 9F93698E657CF4305E7591E6552116222BFC724609FE6E9E5C8F2499D3E2FC60）。


## JavaScript 自动补全（JavaScript 编辑器 2.0.0）

- 自动弹出补全 + 工具栏「补全」按钮 + Ctrl+Space 手动补全。
- 补全内容：
  - JavaScript 关键字 / 字面量
  - 全局对象：console、document、window、Math、JSON、Promise、localStorage 等
  - 当前文件里的函数、类、箭头函数、顶层变量（来自 Outline 解析）
  - 成员补全：console. / document. / Math. / location. / history. 等
  - 代码片段：function、async function、arrow function、if、for、for…of、try/catch、class、console.log、querySelector、addEventListener
- 补全项带类型徽标（KW/FN/VAR/CLS/SNIP/GLB/PROP）与说明。
- 跟随编辑器主题（深色/浅色）。
- HTML 编辑器原有的 HTML 标签、属性、class/id、CSS 属性/值补全保持不变。

### 版本

```
主程序 2.6.0            本地构建（固定版 WebView2 + V8 调试 + EPUB 编辑器）
编辑器外壳 1.3.1         24,374 bytes       4BECBCD6ADFB44B905A07C84607FD01BEB5902CDF31EE059C469D44BD82B63B2
HTML 阅读器 2.4.0        38,351 bytes       E41C830CC382D35033518FD264DD08FC6F9130700501B5C1A6EAEB1A2AD2FF66
HTML 编辑器 2.6.2        65,834,573 bytes   F788BE9744CA1BD04A5DDA20BA5A3BA6B4FF91F4BBA1927A4F8891D0462EF9DB
JavaScript 编辑器 2.0.0 2,112,383 bytes    570DDD4B8340DB4D4F7B8A7F5959C903D27CEDD97E7709461E0CC043E169220B
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```


## 补全体验修正（JS 1.3.3 / HTML 1.23.2）

- JavaScript 自动补全由「输入 2 个字符」提前到「输入 1 个字符」就弹出，延迟降到 120ms。
- HTML 编辑器极简白主题下，补全弹窗文字改为深色 #233244，hover/active 对比度提高。


## 补全实时重新过滤（JS 1.3.4）

- 修复补全弹窗已打开时继续输入，候选仍停在旧前缀的问题。
- 现在输入 i → in → ins 会随每次输入重新过滤候选，ins 首项变为 instanceof，不再停留 if。


## JavaScript 语言功能（1.4.0）

- 参数提示：调用时显示函数签名，高亮当前参数；支持 JSDoc 和 TS 注解。
- 悬停提示：显示符号类型、签名、JSDoc、定义文件与行号。
- 跳转定义：F12 / Ctrl+点击；支持 import 指向的模块。
- 查找引用：Shift+F12，右侧「引用」面板，点击跳到对应文件行。
- 重命名符号：F2，确认后项目内批量替换。
- 自动导入：操作菜单 / Ctrl+. / Ctrl+Alt+I，从项目导出符号中选择并插入 import。
- 「操作」菜单集中提供上述命令；右侧面板支持「大纲 / 引用」切换。
- 状态栏显示当前符号类型。

## 悬停提示修复（JS 1.4.2）

- 修复 shell iframe 内鼠标坐标与编辑器字符位置不一致，悬停现在准确命中鼠标下的符号。
- 悬停支持函数、参数、局部变量、import 符号、内置全局对象和 console.log 等内置 API。

## 悬停精度修复（JS 1.4.3）

- 悬停位置改为 lineAtHeight 取行 + coordsChar 取列，整个标识符的每个字符都能命中。
- 常见内置成员（addEventListener、map、filter、querySelector 等）支持通用签名。
- 未找到声明的标识符也会显示基础悬停信息，不再完全无提示。

## 参数提示与属性悬停（JS 1.4.4）

- 参数提示跟随光标显示，并带「参数」标签，避免和鼠标悬停提示混淆。
- 对象字面量属性支持悬停，例如 once: true → PROP once: boolean。
- 悬停坐标改用 lineAtHeight + coordsChar，整个标识符每个字符都能命中。

## 类型推断增强（JS 1.5.0）

- 多变量声明逐项推断：var raf = null, last = 0, running = false, t = 0。
- 内置属性类型：document.readyState: string、window.innerWidth: number、element.scrollTop: number 等。
- 数组元素类型、三元/逻辑/比较/算术、常见函数返回值、类字段、对象字面量属性、析构声明。
- 悬停按最近定义选择同名符号，避免局部变量/对象属性串位。

## 补全 hover 对比度修复（JS 1.5.1）

- 浅色主题补全列表：active 项被鼠标 hover 时，hover 背景会覆盖 active 背景但文字仍为白色。
- 现在 active:hover 保持深蓝底 + 白字，普通项 hover 保持浅色底 + 深色字。

## Edge 风格 Box model（HTML 编辑器 2.6.2）

- 四层嵌套色带：margin / border / padding / content。
- 每层的 top/right/bottom/left 数值分别贴近对应边显示，不再挤成一行 4 个值。
- content 盒显示实际内容宽高；底部显示 border-box 与 margin-box 尺寸。
- 修正 content 尺寸计算：之前错误地减掉了 margin，现在只减 border/padding。
- 深色/浅色主题都有对应配色。

## Box model 悬停联动预览（HTML 编辑器 2.6.2）

- 鼠标悬停 margin/border/padding/content 时，预览中同步高亮对应层。
- margin/border/padding 画四边色带，content 画填充块；0 值区域也有描边。
- Box model 对应层在编辑器里也高亮，离开后预览自动清除。

## JS 格式化与检查（JavaScript 编辑器 2.0.0）

- 工具栏「格式化」/ Shift+Alt+F：内置 js-beautify 格式化 JS；JSON 走 JSON.parse + 2 空格缩进。
- 工具栏「检查」/ Ctrl+Shift+M：内置 acorn 解析，检查语法错误、未使用变量、未定义变量、重复参数、重复对象键、==/!=、debugger、不可达代码。
- 底部「问题」标签展示问题列表，点击跳转；编辑器行背景同步标记。
- 编辑后 900ms 自动检查。

## 许可证

- 编辑器外壳：GPL-3.0-only，LICENSE 已加入包内。
- JavaScript 编辑器：GPL-3.0-only，LICENSE + THIRD_PARTY_NOTICES.md 已加入包内。
- JS 编辑器内置的 CodeMirror、Acorn、js-beautify 仍是各自 MIT 许可，THIRD_PARTY_NOTICES.md 已列明。

## 可拖动分区与 Console 优化（JavaScript 编辑器 2.0.0）

- Console 上下拖动，文件树/大纲左右拖动，尺寸保存到 localStorage。
- Console 每行显示时间、级别、内容；新增自动滚动开关和复制按钮。
- 运行结束改为最后输出静默 460ms 后标记；晚到输出标注“运行结束后输出”。

## JS 检查与沙箱（JavaScript 编辑器 2.0.0）

- 静态检查新增：未处理 Promise、循环 `<= .length` 越界、undefined/null 属性访问、undefined/null 参数传给会访问属性的函数。
- 运行时错误显示堆栈，并把 Worker/iframe 生成行号映射回源文件行号；Console 错误行可点击定位，编辑器对应行红色标记。
- 无 DOM 的代码默认在 Worker 沙箱执行，超时/死循环可终止；带 DOM API 的代码自动使用 iframe sandbox。
- 语法错误、未定义变量等继续实时检查并标注。

## 更强的静态检查 / HTML Problems（JS 1.9.0 / HTML 2.0.0）

- JS 静态检查新增字面量类型、数组长度、URI/JSON 字面量、非函数调用、undefined/null 属性、new Array(-1)、数组越界、无条件递归等规则。
- more_wrong.js 现在能报出 10 类问题。
- HTML 编辑器 2.6.2 新增 Problems 标签：HTML 标签闭合、重复 id、图片 alt、内联 CSS 括号、内联 JS 语法/类型错误；自动检查 + 点击跳源码。
- HTML 编辑器补齐 GPL-3.0-only LICENSE 与第三方许可说明。

## 语法错误后的恢复检查（JS 1.9.1 / HTML 2.0.1）

- 严格解析失败后使用 acorn-loose 恢复 AST，继续运行未定义变量、类型、数组、JSON/URI 等检查。
- 删除一个大括号时不再只剩一条语法错误，而是语法错误 + 其它可分析问题一起显示。
- HTML 编辑器同样对内联脚本使用恢复分析。

## 问题列表详情（JS 1.10.0）

- 问题列表只显示简短标题，完整消息、规则解释和源码片段放在右侧 Detail 面板。
- 单击问题查看详情，双击或 Detail 按钮跳到源码行。

## ESLint 集成（JavaScript 编辑器 2.0.0）

- 内置 ESLint browser bundle；启用 ESLint 核心规则：no-undef、no-const-assign、no-unused-vars、no-redeclare、no-dupe-*、no-unreachable、no-unsafe-*、no-useless-* 等。
- 保留自定义运行时规则：BigInt/Number 混用、Symbol 隐式转换、不可删除属性、冻结对象写入、new 箭头函数、null 解构、不可迭代展开、Object.assign null、Reflect.get 非对象、负数 String 参数等。
- more_wrong.js 现在可报出 16 个问题。
- 问题列表短标题 + Detail 详情。

## 离线 HTML 校验（HTML 编辑器 2.6.2）

- 不再调用任何在线 validator；内置 html-validate 浏览器 bundle，本地执行。
- HTML 校验按钮检查 DOCTYPE、必需属性、重复 id、可访问性、标签结构等。
- 所有校验数据留在本地，不上传。

## 本地 vnu.jar HTML 校验（HTML 编辑器 2.6.2）

- 使用官方 validator/validator 的 vnu.jar，通过主程序宿主接口在本机 Java 进程执行。
- 扩展自带精简 JRE 17 + vnu.jar，不需要联网；不发送任何 HTML。
- 主程序新增 editor.validate-html 宿主 API，HTML 阅读器负责转发。
- 实际校验测试可报出缺少 lang、重复 id、缺少 alt 等 vnu 结果。

## 内联脚本语言功能（HTML 编辑器 2.6.2）

- `<script>` 内继续使用 htmlmixed 的 JS 高亮。
- 本地符号补全：当前脚本内函数、变量优先出现在补全列表。
- 悬浮卡：函数签名、参数类型、返回类型、JSDoc、内置对象类型。
- 参数提示：光标在函数调用括号内时显示参数列表并高亮当前参数。
- Ctrl+点击标识符跳转到脚本内定义。
- 只分析 `<script>` 内容，不影响 HTML/CSS。

## Monaco TypeScript 分析（HTML 编辑器 2.6.2）

- 内联 `<script>` 的悬浮、参数提示、补全、定义跳转改用 Monaco 自带的 TypeScript language service。
- 使用 `vendor/monaco-ts.min.js`（Monaco `typescriptServices.js` + `lib.ts`，约 6.4MB），不加载 Monaco UI。
- 支持 JSDoc、类型推断、DOM/e2022 lib；保留本地 vnu.jar HTML 校验。

## Monaco 语言服务（2026-11-10）

- HTML 编辑器 2.6.2：内联 `<script>` 使用 Monaco 自带 TypeScript language service，修复 DOM lib 加载和普通字母补全触发。
- JavaScript 编辑器 2.0.0：自动补全改用同一 Monaco TypeScript 服务，保留原有悬浮/参数/定义/引用/重命名。

## Monaco 悬浮统一（2026-11-10）

- JavaScript 编辑器 2.0.0：悬浮提示也改用 Monaco TypeScript language service，显示完整签名、JSDoc、类型、`Monaco TS · Ln`。
- HTML 编辑器 2.6.2：悬浮卡前缀统一为 FN/PROP/VAR/CLS，不再显示硬编码的 `MONACO · TS`。

## 编辑器备份修复（2.1.6）

- 保存项目文件不再在项目里生成 `文件.bak`；旧版本留下的 `.bak` 会迁移到
  `%LOCALAPPDATA%\LocalMediaPlayer\edit-backups`。
- 已清理 physics-review 项目里的 8 个旧 `.bak`，都移动到
  `edit-backups\legacy-projects\physics-review`。
- JS 编辑器 1.14.0：参数提示、定义跳转也改用 Monaco TS。

## 内部资源链接隐藏（HTML 编辑器 2.6.2 / 阅读器 2.3.0）

- 预览/阅读模式不再把 `https://lyra-book-assets.local/...` 暴露成可悬停链接。
- 相对链接存入 `data-lyra-href`，点击时由阅读器解析并打开项目文件。

## 内部链接最终修复（2.6.1 / 2.3.1）

- 内部相对链接完全移除 href，只保留 data-lyra-href；悬停不再出现 `lyra-book-assets.local/#`。
- `#id` 链接改为当前预览/阅读区域内滚动。

## Ctrl+滚轮缩放范围（2026-11-10）

- 主程序保留 wheel 事件分发，但在所有文档注入 capture 级 wheel 防护；文件树/工具栏不缩放，正文由各自阅读器处理。
- PDF 阅读器：只在 PDF 页面区域缩放，侧栏/工具栏不缩放。
- EPUB 阅读器 0.7.0：正文区域 Ctrl+滚轮调整字号，侧栏/工具栏不变。
- HTML 阅读器 2.4.0：正文区域 Ctrl+滚轮缩放，侧栏/工具栏不变。
- HTML 编辑器 2.6.2：只缩放源码字号/预览内容，检查器和工具栏不变。

## 部署事故修复（2026-11-10）

- `deploy.ps1` 之前只编译到 bin，没有复制到实际安装目录 `D:\Users\Administrator\AppData\Local\LocalMediaPlayer`，导致用户测试的仍是 2.1.4。
- 已手动安装 2.1.9 的 `WinUIGui.dll/exe`，并保留自包含 `runtimeconfig.json`（includedFrameworks 10.0.9）。
- 实测：鼠标在文件树 Ctrl+滚轮，界面不缩放。

## 文件管理增强（HTML 阅读器 2.4.0）

- 文件树筛选框，支持按文件名/目录名过滤。
- Ctrl+点击多选，右键批量删除。
- 右键复制文件/文件夹、复制相对路径、移动到根目录。
- 文件/文件夹拖拽移动，拖到根目录也可。
- 从资源管理器拖入文件直接导入当前目录。
- F2 重命名、Delete 删除、Ctrl+D 复制一份。

## JavaScript 调试（1.15.0 / 主程序 2.2.0）

- JS 编辑器底部「调试」标签，纯内嵌调试器，不弹 DevTools、不依赖 CDP。
- 断点、继续、暂停、单步；暂停行高亮、变量面板、表达式求值。
- 「逐行运行」从第 1 行开始逐行暂停，「清空断点」一次清除全部断点。
- 函数/方法/块体箭头回调均插桩，单步可进入函数内部。
- 已知限制：构造函数不能 async，纯插桩调试无法在 constructor 内同步暂停；完全对齐 VS Code 需 V8 调试器。

## 断点入口（JS 编辑器 1.17.1）

- 顶部「断点」按钮切换当前行断点；F9 同样可用。
- 断点栏位于行号最左侧，已加宽并加背景。
- 先切到底部「调试」标签，再运行文件才会暂停。

## 固定版 WebView2 + V8 调试（主程序 2.4.0 / JS 1.21.0）

- 扩展宿主改用内置固定版 WebView2 Runtime 144（third_party/webview2-144，解压约 486MB），恢复 CDP。
- JS 编辑器调试优先走 V8 CDP：constructor、表达式级单步、真实作用域/调用栈。
- CDP 不可用时自动回退源码插桩调试。
- 固定版运行时通过 WEBVIEW2_BROWSER_EXECUTABLE_FOLDER 指定，并关闭站点隔离以便跨域 iframe 可调试。

## JavaScript 编辑器 2.0.0（V8 调试器）

- 固定版 WebView2 144 + CDP V8 调试。
- 构造函数/表达式级断点、逐行运行、进入/跳出/单步。
- 真实作用域变量、调用栈、求值、清空断点、调试输出。
- 每轮重建 runFrame 目标、复用编辑器 iframe 会话，修复第二次断点失效/卡住。
- 单步只显示源码行，自动跳过运行器内部行。

## EPUB 编辑器（1.0.0 / EPUB 阅读器 0.8.0）

- 新扩展 `epub-editor`：解包 EPUB，编辑 XHTML/CSS/OPF/NCX，实时 HTML 预览，重新打包保存。
- EPUB 阅读器工具栏新增「编辑 EPUB」，内嵌整书编辑器；保存后经 `host.workspace.save` 写回原文件并重新载入。
- 编辑器外壳 1.3.0 支持 `.epub`，路由到 EPUB 编辑器插件，项目内双击 EPUB 即可编辑。

```text
EPUB 编辑器 1.0.3  119,864 bytes  5E881E1B535DA6A0C4FD49EFB935DB5DD1D86B55DFACCA7CF41F0CB461BE1726
EPUB 阅读器 0.8.6  414,622 bytes  A89788833E6F990C713C40CCD6922552A2B469428D71540971EDBEE302BB8D53
编辑器外壳 1.3.1    24,374 bytes  4BECBCD6ADFB44B905A07C84607FD01BEB5902CDF31EE059C469D44BD82B63B2
```

## CSS 编辑器（1.0.0 / 主程序 2.6.0）

- 新扩展 `css-editor`：CSS 源码编辑、语法高亮、补全、括号匹配/自动闭合、代码折叠、查找替换、格式化、行号跳转、Ctrl+/ 注释、实时预览、保存。
- 编辑器外壳 1.3.2 支持 `.css`，项目内双击 CSS 直接进入 CSS 编辑器。
- Ctrl+/ 注释快捷键同步补到 HTML 编辑器、JavaScript 编辑器、EPUB 编辑器。

```text
CSS 编辑器 1.0.4   90,583 bytes  AB57A1CF4F72D0CAF78CAC09A0581D74805B2D5D6041870B1E8D99402276446E
编辑器外壳 1.3.3   24,481 bytes  BE97723F55AD42FF4EB890895A57D4CF2A68BA8B66A841523FBDC687AF0753E8
JavaScript 编辑器 2.0.1 2,112,602 bytes B42F7022AE57345A36C006844E7AB09A99BBDCC4FCD958DA31053354E24D9179
HTML 编辑器 2.6.3 65,834,973 bytes 9D8C497FDBE19D80FF391BA4EB7458ED2A11B95FE559458D1CCB54C777F6753D
EPUB 编辑器 1.0.5 120,220 bytes C861C12C7C69B0990966F08BFA97EDAD6E56418B9042B11DE6AA38549D7124BC
```
