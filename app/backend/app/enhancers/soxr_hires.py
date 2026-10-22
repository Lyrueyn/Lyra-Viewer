"""音频超分插槽：soxr_hires —— 非 AI 高质量重采样到 Hi-Res。

★ 2026-10-18 安装版兼容改造（重要）：
  原实现 import soxr + numpy + soundfile。开发版 `.venv` 里有这三个包所以能用，
  但**安装版**用的是工程自带的 embeddable Python（site-packages 为空，刻意零第三方依赖），
  于是点这个增强器就直接报"未安装 libsoxr 依赖"——分发给测试者不可用。

  现在改为走 **ffmpeg**（本项目的必备依赖，安装版已随包）：
    · ffmpeg 编译时带 libsoxr  → 用 `aresample=resampler=soxr`（业界标准 sinc 重采样）
    · 不带 libsoxr            → 用 `aresample` 默认 swr 重采样器，最高精度档
      （precision=28 / filter_size=256，主观听感与 soxr 几乎无差别）
  语义保持不变：**只抬采样率规格（默认 96kHz / 24bit），不补源采样率下已丢失的高频**。

与 AI 的 audio_sr（LavaSR 补高频）互补：
  - audio_sr / LavaSR：AI 模型，真正"补高频"，输出 48kHz。
  - soxr_hires：纯信号处理，高质量重采样，把任意采样率音频提到 Hi-Res（默认 96kHz）。

调用方接口统一：enhance(src, dst, progress_cb) -> dst（与 ai_pipeline 插槽一致）。
依赖：仅 ffmpeg（零 Python 第三方包）。
"""
from __future__ import annotations

import subprocess
from pathlib import Path
from typing import Callable, Optional

from .. import ffmpeg_tools
from ..ai_pipeline import register

# ffmpeg 是否带 libsoxr（进程内只探测一次）
_SOXR_SUPPORT: Optional[bool] = None


def _has_libsoxr(ff: str) -> bool:
    """探测 ffmpeg 是否编进了 libsoxr（-buildconf 里看 --enable-libsoxr）。"""
    global _SOXR_SUPPORT
    if _SOXR_SUPPORT is not None:
        return _SOXR_SUPPORT
    try:
        r = subprocess.run([ff, "-hide_banner", "-buildconf"],
                           capture_output=True, text=True, timeout=30)
        _SOXR_SUPPORT = "libsoxr" in ((r.stdout or "") + (r.stderr or ""))
    except Exception:
        _SOXR_SUPPORT = False
    return _SOXR_SUPPORT


@register("soxr_hires")
class SoxrHiresEnhancer:
    """高质量重采样到 Hi-Res（默认 96kHz / 24bit）。

    target_sr：目标采样率（Hi-Res 标准 96kHz；母带 192kHz 亦可）。
    bit_depth：输出位深（24 → pcm_s24le）。
    """

    def __init__(self, device: str = "cpu", target_sr: int = 96000,
                 quality: str = "HQ", bit_depth: int = 24):
        self.device = device
        self.target_sr = target_sr
        self.quality = quality
        self.bit_depth = bit_depth

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        ff = ffmpeg_tools.find_ffmpeg(required=True)

        if progress_cb:
            progress_cb(0.1)

        if _has_libsoxr(ff):
            # soxr 重采样器：高精度 + 切比雪夫抗混叠
            af = "aresample=resampler=soxr:osr={}:precision=28:cheby=1".format(self.target_sr)
        else:
            # swr 重采样器最高精度档（precision 28 = 28bit 内部精度；filter_size 256 长 sinc）
            af = "aresample=osr={}:precision=28:filter_size=256".format(self.target_sr)

        # 输出编码：wav/flac 输出 24bit PCM（Hi-Res 语义），其它容器用高质量有损
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
            raise RuntimeError("ffmpeg 重采样失败（soxr_hires）：" + msg) from e

        if progress_cb:
            progress_cb(1.0)
        return dst
