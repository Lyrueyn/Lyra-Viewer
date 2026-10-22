"""响度统一增强器：ffmpeg loudnorm 滤镜（EBU R128 国际标准）。

无 AI 模型依赖，P2 可直接启用：把不同来源视频/音频的音量拉平到 -14 LUFS
（B站 / 流媒体平台通用目标响度），播放时不忽大忽小。

T19 修正（2026-10-17）：loudnorm 是**单遍**模式，内部按 192kHz 处理，**不指定 -ar 时
输出采样率会被抬到 192kHz**（实测：44.1kHz 输入 → 192kHz 输出，体积 4.35 倍，
而高频一点没多 —— 对"Hi-Res"是假规格）。现在先探测源采样率再显式 -ar 保持它。

T24（2026-10-17）：ffmpeg/ffprobe 路径统一走 `..ffmpeg_tools`（支持 FFMPEG_PATH 环境变量），
不再自己抄一份硬编码路径；找不到 ffmpeg 时报错会明确提示设置 FFMPEG_PATH。
"""
from __future__ import annotations

import json
import subprocess
from pathlib import Path

from .. import ffmpeg_tools
from ..ai_pipeline import register


def _find_ffmpeg() -> str:
    """统一解析（详见 ..ffmpeg_tools）。找不到会抛清晰错误（含 FFMPEG_PATH 提示）。"""
    return ffmpeg_tools.find_ffmpeg(required=True)


def _find_ffprobe(ffmpeg_path: str) -> str:
    """ffprobe 一般与 ffmpeg 同目录；找不到返回空串（调用方会退回旧行为）。"""
    return ffmpeg_tools.find_ffprobe(ffmpeg_path, required=False)


def _probe_sample_rate(src: Path, ffmpeg_path: str) -> int:
    """取第一个音频流的采样率；失败返回 0（0 = 不指定 -ar，保持 ffmpeg 默认）。"""
    probe = _find_ffprobe(ffmpeg_path)
    if not probe:
        return 0
    try:
        r = subprocess.run(
            [probe, "-v", "error", "-select_streams", "a:0",
             "-show_entries", "stream=sample_rate", "-of", "json", str(src)],
            capture_output=True, text=True, timeout=30,
        )
        streams = (json.loads(r.stdout or "{}") or {}).get("streams") or []
        return int(streams[0].get("sample_rate") or 0) if streams else 0
    except Exception:
        return 0


@register("loudness")
class LoudnessEnhancer:
    def __init__(self, device: str = "cpu"):
        self.device = device

    def enhance(self, src: Path, dst: Path, progress_cb=None) -> Path:
        if progress_cb:
            progress_cb(0.1)
        # 输出容器按目标扩展名选择编码器（wav 用 PCM，其余默认 mp3）
        if dst.suffix.lower() == ".wav":
            audio_codec = ["-c:a", "pcm_s16le"]
        else:
            audio_codec = ["-c:a", "libmp3lame", "-q:a", "2"]
        ff = _find_ffmpeg()
        # ★ 保持源采样率：不加这句，loudnorm 会把输出抬到 192kHz（假 Hi-Res、体积 4 倍）
        sr = _probe_sample_rate(src, ff)
        rate_args = ["-ar", str(sr)] if sr else []
        cmd = [
            ff, "-y", "-i", str(src),
            "-af", "loudnorm=I=-14:TP=-1.5:LRA=11",
        ] + rate_args + audio_codec + [str(dst)]
        subprocess.run(cmd, check=True, capture_output=True)
        if progress_cb:
            progress_cb(1.0)
        return dst
