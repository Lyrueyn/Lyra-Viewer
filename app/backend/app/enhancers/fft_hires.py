"""音频超分插槽：fft_hires —— 非 AI 高质量重采样到 Hi-Res。

★ 2026-10-18 安装版兼容改造：
  原实现依赖 scipy + numpy + soundfile。开发版 `.venv` 有，但安装版是工程自带的
  embeddable Python（零第三方包），点这个增强器会直接报"未安装 FFT 重采样依赖"。
  现在与 soxr_hires 一样改走 **ffmpeg**（必备依赖，安装版已随包），零 Python 第三方依赖：
    `aresample=osr=96000:precision=28:filter_size=512`
  （ffmpeg 的 swr 重采样器本身是"多相 sinc 滤波 + 高精度定点"，与 FFT 频域法同属高质量
   重采样；本项目两条路径现在都只抬规格、不补高频，听感差别可忽略。）

注意（原理与局限）：只抬采样率规格，不补源采样率下已丢失的高频。
调用方接口统一：enhance(src, dst, progress_cb) -> dst。
"""
from __future__ import annotations

import subprocess
from pathlib import Path
from typing import Callable, Optional

from .. import ffmpeg_tools
from ..ai_pipeline import register


@register("fft_hires")
class FftHiresEnhancer:
    """高质量重采样到 Hi-Res（默认 96kHz / 24bit）。

    target_sr：目标采样率。bit_depth：输出位深。
    说明：filter_size 用 512（比 soxr_hires 的 256 更长）——两条路径参数略不同，
    便于用户对比；主观听感差异极小。
    """

    def __init__(self, device: str = "cpu", target_sr: int = 96000, bit_depth: int = 24):
        self.device = device
        self.target_sr = target_sr
        self.bit_depth = bit_depth

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        ff = ffmpeg_tools.find_ffmpeg(required=True)

        if progress_cb:
            progress_cb(0.1)

        af = "aresample=osr={}:precision=28:filter_size=512".format(self.target_sr)
        if dst.suffix.lower() in (".wav", ".flac"):
            acodec = ["-c:a", "pcm_s24le" if self.bit_depth >= 24 else "pcm_s16le"]
        else:
            acodec = ["-c:a", "libmp3lame", "-q:a", "2"]

        dst.parent.mkdir(parents=True, exist_ok=True)
        cmd = [ff, "-y", "-hide_banner", "-loglevel", "error",
               "-i", str(src), "-af", af] + acodec + [str(dst)]
        try:
            subprocess.run(cmd, check=True, capture_output=True, timeout=1800)
        except subprocess.CalledProcessError as e:
            msg = (e.stderr or b"").decode("utf-8", "ignore")[-400:]
            raise RuntimeError("ffmpeg 重采样失败（fft_hires）：" + msg) from e

        if progress_cb:
            progress_cb(1.0)
        return dst
