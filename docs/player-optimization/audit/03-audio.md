# 音频链路审计报告

## 0. 一句话结论

音频主链（`audio_out.h` 单文件自持 WASAPI 轮询 + swr + atempo）功能完整，"单调锚 + 墙钟插值 × tempo"的主时钟设计在水准之上；但有 3 个 P0（24bit 独占写入越界、int16 无钳位回绕、seek 不冲刷设备缓冲）会直接破坏"位完美/无爆音"承诺，而欠载的根因是"2ms sleep 实为 15.6ms + 无 MMCSS/事件驱动"这类系统性缺陷，不是参数没调好。

## 1. 现状音频架构摘要

`audio_out.h`（1047 行）就是全部音频实现：音频线程自开 AVFormatContext 只解音频流 → avcodec → float32 交织 →（变速时）atempo 滤波图 → swr/自研 cubic 重采样 → 16×设备缓冲的环 → 渲染线程 `sleep_for(2ms)` 轮询 `GetCurrentPadding`/`GetBuffer` 写设备；解码 `decodeStep` 就在渲染循环里（`:1004`）。共享默认走混音格式（float32/48k）；独占按"源率→混音率 × 32f/32i/24i/16i"探测，强制注册事件句柄但从不 Wait（`:821`）。主时钟 = `fedMediaUs − (环内+端点未播媒体)`，单调锚 + 墙钟插值。前端只调导出（`VideoWindow.xaml.cs:3328/3330/5290`、`ApplyExclusive:424`）。`wasapi_sink.cpp` 254 行不在构建行（`deploy.ps1:64`）。

## 2. 问题清单

### P0-1 24bit 独占：往 3 字节/样本的流里写 4 字节 → 缓冲越界
- 证据：候选表含 `{false,24}`（`:812`）→ `wBitsPerSample=24`、`nBlockAlign=nChannels*24/8`（`:832-836`）→ `outBytes=3`（`:856`）→ 写入分支只有 float / 2 字节 / else 按 int32 写 4 字节（`:994-996`）。
- 影响：设备一旦接受 packed-24，写入量=分配量的 1.33 倍（音频引擎缓冲越界，堆损坏级），且 24bit 数据被按 32bit 解释=强噪声。本机拒绝 24int（CHANGELOG:2678），所以这颗雷还没引爆。
- 根因：`:810` 注释写"24int(装在32位容器)"，代码造的是 24-bit packed。
- 方案：改成 24-in-32（`wBitsPerSample=32` + `wValidBitsPerSample=24` + `nBlockAlign=ch*4`），写入 `((int32*)p)[k] = (int32)(clamp(s)*2147483647.0) & 0xFFFFFF00`；或直接删掉 24 候选。
- 验收：24bit 设备独占 60s 无异常；32int 路径与源逐样本 null test 残差 0。工作量 0.5 人日。

### P0-2 int16 输出无钳位、无抖动（本机独占的日常路径）
- 证据：`:993-996` `float s = tmp[...]*g;` → `(short)(s*32767.0f)`，无 clamp、无舍入；`DshEngine_SetVolume` 允许到 2.0（`dsh_engine.cpp:1855`）。
- 影响：|s|>1（AAC 过冲、200% 音量、源已削波）→ int16 回绕 = 满幅爆音；截断还有 −0.5LSB 偏置。本机独占唯一可用格式就是 16bit（CHANGELOG:2696）。注：音量本身在 float 域乘（`:993`，方向正确、无位深损失），损失只发生在最后一步 int16 转换。
- 方案：先 `clamp(±1.0)`（或留 0.3dB 余量）+ `lrintf(s*32767)`；16bit 目标加 TPDF 抖动（±1LSB）；>1.0 增益走 5ms look-ahead 限幅。
- 验收：0dBFS 997Hz 在 200% 音量下无越界样本；−1dBFS 正弦 THD+N < −95dB。工作量 0.5 人日。

### P0-3 seek 不冲刷设备缓冲：`devFlush` 是空实现
- 证据：`flush()` 里 `devFlush.store(true)`（`:185`），渲染循环 `if (devFlush.exchange(false)) { }`（`:939`）——空体；同时 `framesOut/devSamples/devMediaUs` 被清零（`:172-173`）。
- 影响：seek 后最多 1 个设备缓冲（独占 20ms / 共享约 10ms）的旧音频继续播出；端点账本清零而 `pad` 仍含旧样本 → seek 瞬间时钟/进度最多 20ms 误差。
- 方案：渲染线程看到 devFlush 时执行 `Stop(); Reset(); Start();`（`wasapi_sink.cpp:204-212` 就有正确写法），并同步重置 `lastPad/devSamples`。
- 验收：20 次随机 seek，audio_dump 在目标前 50ms 内无旧内容；欠载 0；锚偏差 <5ms。工作量 0.5 人日。

### P1-1 轮询节拍 + 无 MMCSS/定时器精度 → 欠载根因
- 证据：`:927` `sleep_for(2ms)`，代码自己注明实际约 15.6ms（`:369`）；全目录 grep 无 `timeBeginPeriod` / `AvSetMmThreadCharacteristics` / `SetThreadPriority`；独占缓冲 20ms（CHANGELOG:2688）。
- 影响：渲染线程每 15.6ms 才醒一次，20ms 缓冲只剩 4.4ms 余量；0.25× 实测 27 次/20s（CHANGELOG:3619）是同一机制的极端表现，低延迟目标根本达不到。
- 方案：①改真事件驱动（独占已 `SetEventHandle`，`:821`）`WaitForSingleObject(ev,100)` + 超时处理——历史"卡死"更可能是 Start 前 Wait/错误 COM 单元，需实测复现；②线程加 `AvSetMmThreadCharacteristicsW(L"Audio")` + `timeBeginPeriod(1)`；③缓冲周期改由 `GetDevicePeriod` 决定。
- 验收：0.5/1/2/4× 各 60s 欠载 0；共享端到端延迟 ~40ms→~25ms（IAudioClock 测）。工作量 1.5 人日（含全档回归）。

### P1-2 设备丢失/切换零处理
- 证据：`:962` `GetBuffer` 失败直接 `continue`；`:941` `hpad` 存下后从不判断；全目录无 `IMMNotificationClient` / `AUDCLNT_E_DEVICE_INVALIDATED`。
- 影响：拔耳机、蓝牙断连、切默认设备、休眠唤醒后渲染循环永久空转而 `ok` 仍为 true（界面显示在播），只能换片恢复。
- 方案：判 `AUDCLNT_E_DEVICE_INVALIDATED` → 置 needReopen，在渲染线程内重建（保留环内数据），注册 `IMMNotificationClient` 监听默认端点变更；失败退避 + 日志。
- 验收：播放中插拔耳机 10 次，2s 内自动恢复、无爆音、欠载计数不失控。工作量 2 人日。

### P1-3 独占探测可能自造假边界（44.1k"全被拒"未必是硬件）
- 证据：`:840` `Initialize(EXCLUSIVE, EVENTCALLBACK, 200000, 200000, ...)` 把周期写死 20ms；16 个候选共用同一个 IAudioClient（`:823-867`），失败后不重新 Activate。
- 影响：44.1k 下 20ms≈882 帧，未必是设备周期整数倍 → 可能返回 `AUDCLNT_E_INVALID_DEVICE_PERIOD`（推测，需实测）。若如此，CHANGELOG:2678 的"只收 48k/2ch/16bit"结论部分是代码造成的，44.1k 源本可免 SRC 位完美。
- 方案：`hnsPeriodicity=0`（用设备默认周期）；每候选前重新 Activate；探测结果按 (deviceId, rate, ch, bits) 缓存进 settings，避免每片 16 次探测。
- 验收：44.1k 源独占要么成功 44100Hz，要么日志能区分 UNSUPPORTED_FORMAT / DEVICE_IN_USE / INVALID_PERIOD。工作量 1 人日。

### P1-4 声道映射是截断复制，没有下混
- 证据：`:709-714` `fix[i*outCh+c] = inter[i*ch + (c<ch?c:ch-1)];`
- 影响：5.1/7.1 源在 2ch 设备上只剩 L/R，中置（人声）、环绕、LFE 全丢。
- 方案：用 swr 做矩阵下混（给真实 in_ch_layout + stereo out、开 rematrix），或按 FFmpeg 默认系数手写 L/R+0.707C+0.707SL/SR+LFE。
- 验收：5.1 测试片人声与 ffmpeg 参考 downmix 电平差 <1dB。工作量 1 人日。

### P1-5 音频线程同时干着解码和文件日志
- 证据：`decodeStep()` 在渲染循环内（`:1004`）；`decodeOnePacket` 持 `dm` 调 `av_read_frame`（`:349`，阻塞 IO）；`dsh_log` 每次 `fopen/fprintf/fclose`（`dsh_engine.cpp:67-73`），音频线程每秒至少一次。
- 影响：磁盘/网络抖动和日志 IO 直接推迟写设备 → 欠载。历史上"解码线程会拖死渲染线程"（`:153-154`）的方向反了：抖动源就是渲染线程自己。
- 方案：恢复独立解码线程（环写端已加锁，`:267-301` 可复用），渲染线程只做 GetBuffer/ReleaseBuffer；`dsh_log` 改内存队列 + 单写线程；过渡版先把 decodeStep 挪到循环开头并限时 ≤3ms。
- 验收：2.0× + 4K 源 60s 欠载 0；渲染线程单轮 p99 <2ms。工作量 2 人日。

### P1-6 停止/换片无淡出
- 证据：`:1040` `ac->Stop()` 前无斜坡；`stop()`（`:188`）直接 `run=false`。
- 影响：波形中途硬停 = "咔"一声，与已实现的欠载/暂停淡入淡出（`:969-991`）不一致。
- 方案：停止前写 5ms 余弦淡出（或最后一轮 ReleaseBuffer 乘斜坡）。验收：连换 30 片，dump 末端幅度 <−60dBFS。工作量 0.5 人日。

### P2（合并）
- **主时钟没扣 atempo 内部延迟**：`fedMediaUs` 在滤波前记（`:725`），引擎只减固定 30ms（`dsh_engine.cpp:268`），而图内 1×≈4000 样本/83ms、2×≈2048/43ms（CHANGELOG:2463 自认）。净差 = 滤波延迟 −30ms ≈ **+53ms（1×、图已建）/ +13ms（2×）/ −30ms（图未建）**，即"建过图后画面比声音超前约 50ms"，且切速前后会跳变——这是可被唇音同步察觉的。蓝牙/HDMI（~100–300ms 输出延迟）更是零补偿。建议用 `fgTempoNow` 估 delay 从 `fedMediaUs` 扣除 + 按端点做"音画偏移"设置项。
- **无 `IAudioClock2::GetDevicePosition`**：位置靠 `framesOut−pad` 推算，没有设备时钟漂移测量，历史 ±15ms/45s 无法定位。独占下取第二路观测并打 drift 日志。
- **16bit 无抖动、不读 ReplayGain**：后端 `loudness.py` 只产出离线文件，播放链不读 R128 标签。
- **独占开关换片才生效**（`dsh_engine.cpp:1863-1880` 注释明确不热切）；无设备选择 UI（只有 `DSH_AUDIO_DEVICE`）。
- **纯音频文件根本不能播**：`LoadMedia` 找不到视频流就 return（`dsh_engine.cpp:2040-2041`）；多音轨只取第一条（`audio_out.h:125-126`）；DSD 只能解成 PCM 再降到 48k（无 DoP）。
- **数据竞争/泄漏**：`info`（`:748` 写 / `dsh_engine.cpp:1895` 读）、`ok`（`:255/:687`）跨线程无锁；`rsTailValid` 在 flush 线程置 false（`:171`）而渲染线程读写；`fail()` 路径（`:746-749`）直接 return 不 Release COM。
- **死代码 `wasapi_sink.cpp`**：不在 `deploy.ps1:64` 构建行，但其 `periodicity=0`（`:88`）与 tanh 软限幅（`:248`）比线上更正确——建议删除或标 legacy。
- **后端增强与播放链断开**：`soxr_hires.py:56-75` 产出 96k/24bit，播放时被 swr 降回 48k/16bit（独占），听感收益≈0；建议按设备能力定目标或标注"仅供归档"。

## 3. 音质/延迟提升方案（按收益排序）

1. **事件驱动 + MMCSS + 1ms timer**：欠载 0/60s（0.5–4×），端到端延迟 −15ms（推测：共享 ~40→25ms，独占 20ms 缓冲可试 10ms 周期）。
2. **24-in-32/32f 独占 + null test**：48k 源 bit-exact，量化上限从 16bit 的 ~−96dB 提到 −110dB 级。
3. **44.1k 独占复测（periodicity=0）**：若成立，44.1k 源 0 SRC，19kHz 残余从 −98.5dBc 直接消失。
4. **int16 钳位 + TPDF**：消除满幅爆音风险，THD+N < −95dB。
5. **5.1 下混矩阵**：人声回来（电平差 <1dB）。
6. **主时钟扣滤波/设备延迟 + 用户偏移**：稳态 |偏差| <10ms（现 30–50ms）。
7. **swr `filter_size 64→256`、`cutoff=0.95`**：19kHz 残余 −98.5→约 −115dBc，属边界收益，0.2 人日。

## 4. 推荐实施顺序（Top 10）

| 编号 | 标题 | 收益 | 成本 | 风险 | 依赖 |
|---|---|---|---|---|---|
| A1 | P0-1 24-in-32 写入修正 | 高（防越界/噪声） | 0.5d | 低 | — |
| A2 | P0-3 seek 冲刷设备缓冲 | 高（每次 seek 的音画精度） | 0.5d | 低 | — |
| A3 | P0-2 int16 钳位 + TPDF | 高（防爆音/THD） | 0.5d | 低 | — |
| A4 | P1-1 事件驱动 + MMCSS | 最高（欠载归零/延迟） | 1.5d | 中 | — |
| A5 | P1-2 设备丢失/切换恢复 | 高（可用性） | 2d | 中 | — |
| A6 | P1-3 独占探测修正 + 缓存 | 中高（位完美） | 1d | 低 | A1 |
| A7 | P1-5 解码移出渲染线程 | 高（长稳） | 2d | 中 | A4 |
| A8 | P1-4 声道下混 | 中（5.1 内容） | 1d | 低 | — |
| A9 | P2 主时钟延迟补偿 | 中（A/V 精度） | 1d | 中（动时钟） | A7 |
| A10 | P1-6 停止淡出 | 低中（听感） | 0.5d | 低 | — |

## 5. 审计覆盖与未覆盖

**覆盖**：`audio_out.h` 全 1047 行；`wasapi_sink.cpp/.h` 全文；`dsh_engine.cpp` 音频相关段（`:244`、`:1855-1899`、`:2040-2074`、`:2139-2161`、`:2583-2588`、`:2812-2827`）；`VideoWindow.xaml.cs` 音量/静音/独占/倍速（`:3295-3349`、`:409-444`、`:765-819`、`:5229-5349`）与 XAML 滑条范围（Volume 0–100、Speed 0.5–4.0）；`deploy.ps1` 构建行；CHANGELOG 音频条目交叉核对；`soxr_hires.py`/`loudness.py` 头部。

**未覆盖/未验证**：全程只读，未启动播放器/后端、未出声；文中"实测"数字均引自 CHANGELOG 而非本次复测。**硬件边界（不可代码绕过）**：本机独占据称仅 48k/2ch/16bit（须由 P1-3 复测确认是否含代码因素）、无 DSD DoP 直通、共享模式必过系统混音器即非位完美；**代码可修**：其余全部。ASIO 当前线无任何源码（`app/ASIO-SDK_2.3.4_2025-10-15` 只是 SDK，`asio_sink.cpp` 仅存在于 `_legacy-player`），落地属从零工作。未做火焰图/ETW 采样，P1-5 的 p99 需实测；未审 WinUIGui 其余音频无关部分与后端 enhancer 全实现。
