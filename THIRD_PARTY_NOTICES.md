# 第三方组件与分发许可说明（1.0.0 调研记录）

本项目（璃鸢镜 / Lyra Viewer）在开发与本地安装包中使用了许多第三方组件。
本文件记录 **1.0.0 发布时对 NVIDIA / FFmpeg / Qt 分发许可的核查结论**，以及为什么 GitHub Release 暂时只发布源码、不上传安装包二进制。

> 本文件是工程调研记录，不是法律意见；正式公开分发二进制前应让专业律师复核。

## 1. NVIDIA RTX Video SDK（安装包含 nvngx_vsr.dll / nvngx_truehdr.dll）

来源：`NVIDIA_RTX_Video_SDK_License.pdf`（RTX Video SDK v1.1.0，安装包内也带有该 PDF）。

关键条款（原文节选）：

- §1(c)：允许把 SDK 中的软件/材料以 **目标代码形式** 集成进一个软件应用并分发，但必须满足 §2 的分发要求。
- §2：应用必须具有超出 SDK 自身的重要附加功能；分发 SDK 时必须附带与 NVIDIA 许可 **至少同等保护力度** 的条款（含许可限制与 NVIDIA 知识产权保护）；需通知 NVIDIA 违规分发。
- **§4(e)：不得以任何方式使用 SDK，使其受开源软件许可约束**，例如要求 SDK（i）以源码形式披露/分发，（ii）被许可用于制作衍生作品，或（iii）可免费再分发。

结论：

- NVIDIA **允许**把 `nvngx_*.dll` 作为应用程序的一部分分发，但有 EULA/保护条款要求；
- **与本仓库 GPLv3 许可组合公开分发存在直接冲突风险**：GPLv3 要求整个作品可按 GPL 再分发/提供源码，而 §4(e) 明确禁止让 SDK 受此类开源许可约束；
- 因此 1.0.0 的 **GPLv3 开源 Release 不附带包含 NVIDIA SDK 的安装包**；若要公开二进制，需把 RTX 功能拆成单独的非 GPL 分发渠道并取得 NVIDIA 明确许可/法务确认，或做一个**不含 RTX SDK 的 FOSS 版**。

## 2. FFmpeg（安装包含 avcodec/avformat/avfilter/avutil/swscale/swresample 等 DLL 与 ffmpeg.exe）

本机安装包内的 FFmpeg 来自 gyan.dev `essentials_build`：
`ffmpeg version 2022-08-31-git-319e8a49b5`，configuration 含
`--enable-gpl --enable-version3 --enable-libx264 --enable-libx265 ...`。

FFmpeg 官方说明（https://ffmpeg.org/legal.html）：

- FFmpeg 默认 LGPLv2.1+，但一旦启用 GPL 部分（`--enable-gpl`），**整个 FFmpeg 按 GPL 分发**；
- 分发二进制时必须：分发 **与二进制完全对应的 FFmpeg 源码**、说明 configure 编译参数、附带许可证文本，并在下载页/关于框标注 FFmpeg 与许可证；
- gyan.dev 构建页也明确写明：**All builds are 64-bit, static and licensed as GPLv3**（https://www.gyan.dev/ffmpeg/builds/）。

结论：

- 当前安装包里的 FFmpeg 是 **GPLv3 构建**，公开分发二进制需要同时提供对应 FFmpeg 源码/编译说明/许可证文本；
- 当前 GitHub 仓库/Release 没有附带 FFmpeg 对应源码，因此 **不能直接上传安装包**；
- 若要发布二进制，推荐改用 `--disable-gpl --disable-nonfree` 的 **LGPL 共享构建**（并附 LGPL 文本、源码获取方式、允许替换动态库），或者随 Release 一起上传完全对应的 FFmpeg 源码包与 configure 说明。

## 3. Qt（本项目只在构建时使用；安装包不含 Qt 运行时 DLL）

- 安装包最终目录 **没有任何 `Qt6*.dll`**；Qt 仅作为编译工具链/头文件与静态第三方库来源使用。
- `dsh_engine.dll` 静态链接的是 `Qt6BundledFreetype`、`Qt6BundledLibpng` —— 这是 Qt 打包的 **FreeType / libpng** 第三方库（FreeType License、libpng License），不是 Qt 框架本身的 LGPL/GPL 代码。
- 因此当前二进制发布 **不触犯 Qt 的 LGPL/GPL 分发义务**（Qt 不在分发包里），但需要保留 FreeType/libpng 的版权与许可声明。
- 若以后改为分发 Qt DLL：Qt 开源版为 LGPLv3/GPLv3；LGPL 路线需要提供许可证文本、允许用户替换/重链接 Qt 库、提供源码获取方式等（参见 Qt 官方 LGPL 义务说明：https://www.qt.io/development/open-source-lgpl-obligations）。

## 4. 其他随安装包分发的组件（需要补 NOTICE）

- Python 3.10 运行时：PSF License（安装包内已有 `backend/python/LICENSE.txt`）；
- .NET Runtime / Windows App SDK / WebView2 / DirectML / onnxruntime：Microsoft 或 MIT 许可，允许随应用再分发，但需保留版权/许可声明；
- yt-dlp、whisper（openai-whisper）等 Python 包：各自 MIT/Unlicense 等许可；
- FreeType、libpng（静态链接进 dsh_engine.dll）：FreeType License / libpng License；
- NVIDIA RTX SDK：见第 1 节。

## 5. 当前结论 / 后续计划

1. **1.0.0 GitHub Release 只发布源码**（已完成），不上传安装包/便携版二进制；
2. 若要公开二进制，优先做 **FOSS 版**：
   - 移除/禁用 NVIDIA RTX Video SDK 组件与对应 DLL；
   - 使用 LGPL FFmpeg 共享构建（或附上完全对应的 GPL 源码与编译说明）；
   - 增加 `THIRD_PARTY_NOTICES` 与各许可证文本；
   - 这样 GPLv3 源码 + 二进制才具备清晰的分发链路。
3. 若必须保留 RTX VSR：需要 NVIDIA 书面许可，并把该组件从 GPL 发布物中拆成独立分发/独立 EULA，不能混在 GPLv3 Release 里。


## 6. 2026-10-22 更新：公共二进制已发布 + SDK 自带路径

- GitHub Release `v1.0.0` 已上传安装版/便携版（`LyraViewer-Setup-1.0.0.exe` / `LyraViewer-Portable-1.0.0.zip`），
  其中 **不包含** NVIDIA RTX Video SDK 的 nvngx DLL；
- 播放器设置 → 视频画质 → 「RTX Video SDK 路径」：用户选择自己下载的 SDK 根目录后，
  程序自动找到 `bin\Windows\x64\rel\nvngx_*.dll` 并复制到程序目录使用（NVIDIA 文档默认从程序目录加载），
  选择结果写入 `data/player_settings.json` 的 `rtx_sdk_path`，可清除；
- FFmpeg 仍为 GPLv3 构建；安装目录与仓库均提供 `FFMPEG_SOURCE_INFO.txt`（版本、configure、对应源码链接）；
- Qt 仍未随包分发；安装包内静态链接的 FreeType/libpng 按各自许可保留声明。
