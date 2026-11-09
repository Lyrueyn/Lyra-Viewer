# 本地媒体播放器 · 工作区说明

> 2026-10-22 整理：原来散落在 `F:\DSH\` 顶层的播放器工程文件（171 个目录 + 499 个文件）全部集中到这里。
> 整理方式：**同盘移动/改名**，没改动任何文件内容；工程里写死的旧绝对路径已批量更新。

## 目录结构

```
F:\DSH\LocalMediaPlayer-Workspace\
├─ app\                    主工程源码（原 F:\DSH\local-media-player）
│  ├─ WinUIGui\            WinUI 3 前端（.NET 10）
│  ├─ dsh_engine\          C++ Vulkan/WASAPI 引擎（MinGW 构建，产物 dsh_engine.dll）
│  ├─ backend\             Python 标准库后端（stdlib_server.py / services.py / enhancers\ / models\）
│  │  └─ .venv\            开发用 Python 虚拟环境（移动后已验证可用）
│  ├─ data\                开发实例数据（库、下载、导出、增强产物等）
│  ├─ docs\  runtime\  tools\  picture\ ...
│  └─ CHANGELOG.md         版本变更记录（含每轮补记）
├─ builds\                 历史编译输出目录（_build_xxx，87 个；最新的是 _build_fix14）
│  └─ _stray_root_files\   原先误落在 F:\DSH 根目录的 491 个 xbf 杂散文件
├─ packaging\              打包工作目录（原 F:\DSH\_pkg_t34）
│  └─ _pkg_t34\
│     ├─ final\            安装包 payload（程序 + 后端 + 模型 + ffmpeg + 嵌入 Python 等）
│     ├─ stage\payload.zip 打包用压缩包（438.5 MB）
│     ├─ lmp-setup-user.iss / lmp-setup-admin.iss 等 Inno Setup 脚本（路径已更新到本工作区）
│     ├─ inno-out-v49\     最近一次安装包输出
│     └─ 本地媒体播放器-Setup(HDR安全处理).exe / 本地媒体播放器-便携版.zip   ← 当前交付物
├─ backups\                历史备份（4 个 FULLBACKUP 共 62.4GB + 73 个普通备份 6.7GB + 1 个 zip 308MB）
├─ refs\                   参考资料/工具
│  ├─ RTX_Video_SDK_v1.1.0\  NVIDIA RTX Video SDK（VSR/TrueHDR；含 Vulkan 头文件、静态库、示例、License）
│  ├─ RTX_Video_SDK_v1.1.0.zip
│  ├─ InfVSR\                InfVSR 仓库浅克隆（12.4MB，模型未下载）
│  ├─ _tools\                Inno Setup 7 安装包等工具
│  └─ _engine_out\           历史 dsh_engine.dll
├─ misc\                   历史临时脚本/日志/截图等（原 F:\DSH 根目录散落文件）
└─ move_log.json           本次移动清单（moved 668 + 1 个已手工修正）
```

## 常用命令（新路径）

```powershell
$ws = 'F:\DSH\LocalMediaPlayer-Workspace'
$app = "$ws\app"
$py  = "$app\backend\.venv\Scripts\python.exe"

# 编译前端
dotnet build "$app\WinUIGui\WinUIGui.csproj" -c Debug -o "$app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"

# 开发后端 / 增强管线
& $py -c "import sys; sys.path.insert(0, r'$app\backend'); from app import services; print('OK')"

# 打包（同步 final → payload.zip → Inno Setup）
# (详细流程见 CHANGELOG 补记；ISCC 在 D:\tools\Inno Setup 7\ISCC.exe)
```

## 本次移动后做过哪些修正

1. 工程/文档/脚本里的 `F:\DSH\local-media-player` → `F:\DSH\LocalMediaPlayer-Workspace\app`
2. 打包 iss 的 `SrcDir` / `OutputDir` → 本工作区 `packaging\_pkg_t34\...`
3. 引用历史备份目录的脚本 → 指向 `backups\...`
4. **验证**：`.venv\Scripts\python.exe` 可用、backend 可导入、`dotnet build` 成功（18 秒）、安装包产物完好
5. 未动：`F:\DSH\tomato-reverse`、`F:\DSH\线性代数课堂`（不是播放器工程）；安装版程序和数据仍在 `%LocalAppData%\LocalMediaPlayer`，不受影响

## 注意

- `backups\` 占 69GB、`packaging\` 占 14.5GB、`app\` 占 18.6GB；如需腾空间，可优先删旧 FULLBACKUP/旧 `inno-out-v*`，确认后再删。
- `refs\RTX_Video_SDK_v1.1.0` 里的 License 是 NVIDIA Proprietary（LicenseRef-NvidiaProprietary），对外分发安装包前需要先确认 SDK DLL 的再分发条款。

## 本机真实构建链（2026-10-22 核对，勿只查 C:\ 默认路径）

| 用途 | 工具 | 位置 |
|---|---|---|
| WinUI 前端 | dotnet SDK | `C:\Program Files\dotnet` |
| dsh_engine.dll | MinGW g++ | `E:\Qt\Tools\mingw1310_64\bin\g++.exe`（手动 `g++ -shared`，未进 PATH） |
| 着色器 | glslc | `D:\VulkanSDK\1.4.357.0\Bin`（已在 PATH） |
| Vulkan 头/库 | Vulkan SDK 1.4.357.0 | `D:\VulkanSDK\1.4.357.0`（`VULKAN_SDK` 已设） |
| MSVC / MFC / ATL / Win SDK | VS Community 2026 (MSVC 14.51, Win SDK 10.0.26100) | `D:\Program Files\Microsoft Visual Studio\18\Community` |
| CUDA Toolkit | 未安装 | — |

## RTX VSR 实测（2026-10-22，RTX 5080 16GB / 驱动 610.62）

用 VS2026 编译 `refs\RTX_Video_SDK_v1.1.0\samples\RTX_Video_API` 静态库，再写最小 DX11 测试程序
`refs\rtx_vsr_test\test_dx11_vsr2.cpp`（调用 `rtx_video_api_dx11_create/evaluate`）。

| 场景 | 质量 1 | 质量 2 | 质量 3 | 质量 4 |
|---|---|---|---|---|
| 960x540 → 1920x1080 | 0.42 ms | 0.50 ms | 0.92 ms | 1.20 ms |
| 1920x1080 → 3840x2160 | 1.27 ms | 1.57 ms | 3.20 ms | 4.28 ms |

- 4K 压测时 GPU 均值 82%、峰值 100%、显存峰值 4.37GB（全速压测；实际 60fps 播放占用低得多）
- 画质：VSR 比 bicubic 明显锐利；PSNR（640→1280 vs 1080p 原片 Lanczos 缩）bicubic 40.25dB > VSR q1 38.4dB
  → VSR 是"锐化+去压缩伪影"风格，不是严格还原；质量 4 有轻微颗粒/边缘光晕，建议日常用 1~2 档
- 对比图：`refs\rtx_vsr_test\compare_bicubic_vs_vsr.png`、`compare4k_q1_vs_q4.png`
