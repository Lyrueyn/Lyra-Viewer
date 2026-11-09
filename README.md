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

- **EPUB 0.6.9**：目录长标题换行、侧栏不横向溢出、正文序号修复；翻页冷却与串行化、跨章过渡、快速翻页不积压；1/3 动画冷却、页内/跨章动画协调。
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
| EPUB 扩展 | 0.6.9 | 已发布 |
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
主程序 2.1.4            433,963,636 bytes  1FE72DE5F26B98A31FF88F6FA80EEEB6561666FB0D64EE1D4D85EA7E2591A2FC
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
主程序 2.1.4            433,963,636 bytes  1FE72DE5F26B98A31FF88F6FA80EEEB6561666FB0D64EE1D4D85EA7E2591A2FC
编辑器外壳 1.1.0         8,212 bytes        C6089BD8A2C8EE13439809B9EA9065D34711A3284D305ABEC22D4872EE2924CA
JavaScript 编辑器 1.2.2  103,643 bytes      F7F8B038DCA5C8DD25AA6B989DE41FA2410EAF2943AF59265E523CFC52C181A1
HTML 阅读器 2.1.7        34,184 bytes       7B435F8672F2DC28F4BF208FEC4B973D438C7B210FB3C387B9E7D72A31B25CD7
HTML 编辑器 1.22.0       243,986 bytes      71F7F547FB5A67708FCDD7303566839AA19E8B36232C491A33F3AE33FB0B27C1
编辑器扩展 1.1.1         6,375 bytes        DC9620790C34EC7170F54D9FE356EE62C6334F9AF9FDED76CE1E9C43185BCCA7
笔记本 2.3.39            55,179 bytes       E7C03B41CA045E2D7ED2147FBFBACA50CC76F6C80DF6DBA05B93DEA0181341BD
```
