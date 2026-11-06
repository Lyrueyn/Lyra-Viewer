# 璃鸢镜（Lyra Viewer）v1.4.9

> 🤖 **本项目由 AI（DeepSeek Harness）制作/协作开发。**
>
> 一个面向 Windows 的本地/在线媒体播放器：
> **WinUI 3 前端 + 自研 C++ 音视频引擎 + 本地 Python 后端 + WebView2 阅读扩展**。
> 支持 B 站 / YouTube 下载、弹幕、评论、字幕、AI 字幕（Whisper）、RTX VSR、HDR、音频增强，以及 EPUB / PDF 阅读。

## 下载安装

最新版本：**璃鸢镜 1.4.9**

| 内容 | 地址 |
|---|---|
| 主程序 Release | https://github.com/Lyrueyn/Lyra-Viewer/releases/latest |
| 安装包 | `LyraViewer-Setup-1.4.9.exe` |
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
| 主程序 | 1.4.9 | 已发布 |
| EPUB 扩展 | 0.6.9 | 已发布 |
| PDF 扩展 | 0.4.7 | 已发布 |
| TXT 扩展 | 0.1.2 | 已发布 |
| 番茄小说下载器 | 0.2.0 | 已发布（前端开源，核心加密随主程序） |
| HTML 阅读器 | 1.3.18 | 已发布 |
| HTML 编辑器层 | 1.18.1 | 已发布 |
| 笔记本 | 2.3.27 | 已发布 |

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
