"""音频超分插槽：LavaSR（AI，完整实现），插槽名 audio_sr。

LavaSR（https://github.com/ysharma3501/LavaSR，Apache-2.0）：轻量级音频增强模型，
支持任意 8-48kHz 输入 → 48kHz 清晰输出，~5000x 实时（GPU）/ ~60x（CPU），~50MB。
它是真正"补高频"的 AI 超分（Vocos 架构），质量超越 6GB 级扩散模型。

本插槽保留 AI 超分能力；如需非 AI 的高质量重采样到 Hi-Res，用同目录的 soxr_hires
（libsoxr 纯信号处理，只抬采样率规格、不补高频）。

依赖：torch + soundfile + LavaSR 包
    uv pip install git+https://github.com/ysharma3501/LavaSR.git
（本机 backend/.venv 已装 torch 2.13.0+cu130 / soundfile / LavaSR）
"""
from __future__ import annotations

from pathlib import Path
from typing import Callable, Optional

from ..ai_pipeline import register


@register("audio_sr")
class AudioSREnhancer:
    """音频超分增强器：LavaSR（AI）。输入任意采样率音频 → 48kHz 增强输出。

    denoise=False 默认纯超分/增强（不清洗正常音频）；batch=True 供长音频。
    """

    def __init__(self, device: str = "cuda", denoise: bool = False, batch: bool = False):
        self.device = device
        self.denoise = denoise
        self.batch = batch
        self._model = None  # 懒加载缓存

    def _get_model(self):
        if self._model is None:
            try:
                from torch import cuda as _cuda
                from LavaSR.model import LavaEnhance2
            except ImportError as e:
                raise RuntimeError(
                    "未安装 LavaSR（AI 音频超分），请先执行：\n"
                    "  uv pip install git+https://github.com/ysharma3501/LavaSR.git\n"
                    "（依赖 torch + soundfile；本机 backend/.venv 已有）"
                ) from e
            dev = self.device if _cuda.is_available() else "cpu"
            self._model = LavaEnhance2("YatharthS/LavaSR", dev)  # 首次自动从 HF 下载 ~50MB
        return self._model

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        import tempfile
        import numpy as np
        import soundfile as sf

        if progress_cb:
            progress_cb(0.05)
        model = self._get_model()
        if progress_cb:
            progress_cb(0.2)

        # LavaSR 是单声道模型：读原文件，多声道则逐声道处理再合并，保留左右声道。
        audio, in_sr = sf.read(str(src), dtype="float32", always_2d=True)
        if audio.ndim == 1:
            audio = audio[:, None]
        n_ch = audio.shape[1]

        tmpdir = tempfile.mkdtemp(prefix="lava_ch_")
        try:
            ch_results = []
            for ch in range(n_ch):
                # 写出该声道临时 wav（LavaSR.load_audio 需要文件路径）
                ch_path = Path(tmpdir) / f"ch{ch}.wav"
                sf.write(str(ch_path), audio[:, ch], in_sr, subtype="PCM_16")
                if progress_cb and n_ch > 1:
                    progress_cb(0.2 + 0.7 * ch / n_ch)
                x, _ = model.load_audio(str(ch_path))
                y = model.enhance(x, denoise=self.denoise, batch=self.batch).cpu().numpy().squeeze()
                ch_results.append(y)

            if n_ch == 1:
                output = ch_results[0]
            else:
                # 按最短声道截齐后合并（各声道长度略有差异，取最小）
                min_len = min(len(c) for c in ch_results)
                output = np.stack([c[:min_len] for c in ch_results], axis=1)
        finally:
            import shutil
            shutil.rmtree(tmpdir, ignore_errors=True)

        if progress_cb:
            progress_cb(0.9)

        dst.parent.mkdir(parents=True, exist_ok=True)
        sf.write(str(dst), output, 48000)  # LavaSR 输出固定 48kHz
        if progress_cb:
            progress_cb(1.0)
        return dst
