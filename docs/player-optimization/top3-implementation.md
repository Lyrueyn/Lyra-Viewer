# 播放器审计 Top3 实施记录（2026-10-31）

分支：`opt/player-top3`，提交 `c8d3f06551703e66f4426b83c292daa70dcac5ed`（基于 1.1.13 的 `main@d7883ff`）
未发布、未合并 main、未改用户安装目录；`packaging/_pkg_t34/final` 的引擎已恢复为 1.1.13 原版，优化只保留在源码分支和隔离测试目录。

## 1. 改动

### 1.1 引擎导出面 / ABI 自检

- `dsh_engine.cpp` 新增 3 个 C 导出：
  - `DshEngine_GpuDead()`：返回真实 `g_gpuDead`（修掉源码只剩注释、新 DLL 无导出导致判死横幅静默失效）
  - `DshEngine_GetApiVersion()`：返回 `2`，界面 Init 后强校验
  - `DshEngine_GetEndFlags()`：bit0=音频 EOF、bit1=视频 EOF（供前端结束判定双条件）
- C# `DshNative` 增加 `ExpectedEngineApiVersion=2`、`GetApiVersion`、`GetEndFlags` 声明
- `VideoPanel_Loaded` 在 Init 后调用 `CheckEngineApiVersion()`；不匹配时显示横幅“引擎与界面版本不匹配”，并写明旧 DLL 缺 `GetApiVersion`，不会被每秒的 `CheckGpuDead` 或换片 `ResetGpuDeadUi` 收起

### 1.2 「重新启动播放器」

- 启动参数补 `--video <当前文件>`（用 `ProcessStartInfo.ArgumentList`，路径含空格也安全）
- 判活从“按进程名找 PID≠self”改为“PID≠self 且主窗口标题以 `璃鸢镜 · ` 开头”，避免主窗口进程被误判为新实例
- 旧行为在多窗模式下会在第一次 100ms 轮询就命中主窗口 → `Exit()`；现在只有新视频窗真正出现才退出自己

### 1.3 帧队列背压

- `qCap()` 预算 192MB → 默认 320MB，可用 `DSH_FRAME_QUEUE_MB`（32~1024）覆盖；4K 约 9 帧，1080p 仍 16 帧
- `qPush()` 改为返回 `bool`：队列满直接拒绝，不再静默覆盖最旧帧
- `DecodeAhead()` 的循环判据从 `g_qCount>=QMAX` 改为 `g_qCount>=qCap()`；`qPush` 返回 false 时立即 break，并先 `av_frame_unref`

### 1.4 音轨 EOF / 结束判定

- `audio_out.h` 新增 `endedFlag`，AAC/FFmpeg 侧 `av_read_frame` EOF 时置位；`openFile`/`flush` 时清零；公开 `fileEnded()`
- 主时钟 `dsh_clock_ms_double()` 在音频 EOF 后不再把旧的 `mediaUs()` 当观测锚，继续用已有锚 + 墙钟外推（避免媒体时间冻结）
- 前端 `UpdateSeekUi` 结束判定改为：
  - `nearEnd` 仍可直接结束；
  - `stalled` 只有在 `audioEof && videoEof` 双条件成立时才允许自动重播/连播，短音轨提前 EOF 不再每 3m57s 自循环
  - 日志增加 `audioEof/videoEof` 字段

## 2. 构建与身份

- 引擎新 DLL：`app\dsh_engine\dsh_engine_vsr.dll`（5,246,767 bytes）
  - SHA-256 `405A7778FE55814EECED2E101344D00A3B7C498799AAAEE6C475D9700A6893D7`
  - 导出验证：`DshEngine_GetApiVersion`、`DshEngine_GetEndFlags`、`DshEngine_GpuDead` 均在（`top3-evidence/engine-exports.txt`）
- 主程序 Debug 构建：`D:\Temp\lyra_host_top3_build\WinUIGui.dll`
  - SHA-256 `1C9B8B4587D8188BCC345578AB5367560803836E08C948C6BB6CB227993D6009`
  - 0 个编译错误（`top3-evidence/host-build.log`）
- 测试目录：`D:\Temp\lyra_1113_test`（隔离，不是用户安装目录）

## 3. 已验证

| 项 | 方法 | 结果 |
|---|---|---|
| 引擎新导出 | `objdump -p` | 三个新符号都出现 |
| ABI 自检（旧 DLL） | 测试目录放 10-23 旧引擎（无 GetApiVersion），启动 `--video testclip.mp4` | 界面显示“引擎与界面版本不匹配”，日志记录 `EntryPointNotFoundException`；截图 `top3-evidence/abi-mismatch-old-engine.png` |
| ABI 正常（新 DLL） | 新引擎启动同视频 | 正常播放，无 ABI 横幅；截图 `top3-evidence/new-engine-playback.png` |
| 重启按钮 | 旧引擎触发 ABI 横幅 → UIA 找到 `RestartPlayerBtn` 并 Invoke | 旧 PID 45648 退出；新 PID 42612 出现，命令行确认带 `--video ...testclip.mp4`；证据 `top3-evidence/restart-button-test.txt` |
| 主程序编译 | `dotnet build -c Debug` | 0 errors（30 个旧警告） |

## 4. 未验证 / 限制

- **没有做 4K/2K 队列 A/B 采样**：背压/容量变化是源码级修正，未在真实 4K 素材上量化解码帧数、UI 帧时与内存峰值。
- **没有真实触发“短音轨 EOF + 视频继续”样本**：结束判定双条件、时钟 EOF 回退是编译和代码路径验证，未复现原 37 次循环的同一文件。
- **没有真实制造 GPU 设备丢失**：GpuDead 导出与 UI 提示用旧 DLL 缺导出场景验证，未注入 `DXGI_ERROR_DEVICE_REMOVED`。
- 重启按钮用旧引擎的 ABI 横幅路径触发，已证明“新进程带 --video + 旧进程退出”；真实 GPU 判死路径仍应再测。
- `packaging/_pkg_t34/final` 未包含本优化（仍为已发布的 1.1.13 状态），避免污染已发布产物；优化在 `opt/player-top3` 分支。

## 5. 证据文件

- `docs/player-optimization/top3-evidence/abi-mismatch-old-engine.png`
- `docs/player-optimization/top3-evidence/new-engine-playback.png`
- `docs/player-optimization/top3-evidence/restart-button-test.txt`
- `docs/player-optimization/top3-evidence/engine-exports.txt`
- `docs/player-optimization/top3-evidence/host-build.log`
- `docs/player-optimization/top3-evidence/test-dm_debug.log`

结论：Top3 的源码修复、编译、ABI 提示、重启按钮动态路径已闭合；队列性能与真实 EOF/GPU 丢失仍需专门样本实测，未按 PASS 夸大。
