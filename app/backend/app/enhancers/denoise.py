"""降噪增强器：DeepFilterNet（完整实现）。

依赖：deepfilternet + librosa + soundfile（需 torch 环境）。

兼容性说明（T19 更新，2026-10-17）：deepfilternet 0.5.6 顶层 `import torchaudio` 并且
`from torchaudio.backend.common import AudioMetaData`，而 **torchaudio 2.1+ 已移除 backend 子模块**
（本机装的是 torchaudio 2.11.0）。本模块用 librosa 读音频、不走 df 的 IO，所以给 df
**补齐它需要的 torchaudio 缺口**（见 `_ensure_torchaudio_shim`），真实已存在的属性一律不覆盖。

★ 为什么要"补缺口"而不是"注入空包"：后端是长驻进程，而 **LavaSR 会 import torchaudio**
（`backend/app/enhancers/audio_sr.py` 依赖它）。旧实现在 `"torchaudio" in sys.modules` 时直接跳过注入，
于是"先跑过 audio_sr、再跑 denoise"必然报 `ModuleNotFoundError: No module named 'torchaudio.backend'`。
T19 已实测复现该顺序依赖并修掉，现在与导入顺序无关。
"""
from __future__ import annotations

import importlib
import sys
import types
from pathlib import Path
from typing import Callable, Optional

from ..ai_pipeline import register


def _ensure_torchaudio_shim() -> None:
    """给 df 补齐 torchaudio 缺口（只补缺失、不覆盖真实属性），与导入顺序无关。"""
    ta = sys.modules.get("torchaudio")
    if ta is None:
        try:
            ta = importlib.import_module("torchaudio")      # 真实包存在就用真实的（2.11 可正常导入）
        except Exception:
            ta = types.ModuleType("torchaudio")
            sys.modules["torchaudio"] = ta

    def _ensure(name: str):
        mod = sys.modules.get(name)
        if mod is None:
            try:
                mod = importlib.import_module(name)         # 真实子模块存在就复用
            except Exception:
                mod = types.ModuleType(name)
            sys.modules[name] = mod
        parent, _, leaf = name.rpartition(".")
        if parent and parent in sys.modules and getattr(sys.modules[parent], leaf, None) is None:
            setattr(sys.modules[parent], leaf, mod)         # 只挂缺失的那一层
        return mod

    backend = _ensure("torchaudio.backend")
    common = _ensure("torchaudio.backend.common")
    if not hasattr(common, "AudioMetaData"):
        common.AudioMetaData = type("AudioMetaData", (), {})
    if getattr(backend, "common", None) is None:
        backend.common = common
    compliance = _ensure("torchaudio.compliance")
    kaldi = _ensure("torchaudio.compliance.kaldi")
    if not hasattr(kaldi, "resample_waveform"):
        kaldi.resample_waveform = lambda *a, **k: None      # df 的 IO 路径，我们不调用
    if getattr(compliance, "kaldi", None) is None:
        compliance.kaldi = kaldi


@register("denoise")
class DenoiseEnhancer:
    def __init__(self, device: str = "cpu", atten_lim_db: float = 8.0):
        """atten_lim_db：噪声衰减上限（dB）。

        DeepFilterNet 的 enhance() 若不传 atten_lim_db，默认无限衰减——
        会把音乐/歌声等"非噪声"的主体信号一起抹掉（听感：人声几乎没了）。
        8dB 实测保留 ~98% 原始信号（corr 0.98），降噪非常温和、基本不动人声；
        12dB 保留 ~93%（corr 0.93）。数字越小越温和（接近原样），越大越激进。
        """
        self.device = device
        self.atten_lim_db = atten_lim_db

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        try:
            _ensure_torchaudio_shim()
            import librosa
            import soundfile as sf
            import torch
            from df.enhance import enhance, init_df
        except ImportError as e:
            raise RuntimeError(
                "未安装降噪依赖，请先执行：\n"
                "  pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu130\n"
                "  pip install -r requirements-ai.txt"
            ) from e

        if progress_cb:
            progress_cb(0.1)

        # df 0.5 的 init_df 无 device 参数（自动用 CUDA if available）。
        # ★ log_file=None：默认会在 %LOCALAPPDATA%\DeepFilterNet\...\enhance.log 写日志，
        #   而该文件被"任何一个已经跑过 df 的后端进程"长期持有（loguru 的 FileSink 不释放）
        #   → Windows 下第二个进程 init_df 会 PermissionError 直接失败。后端自己有日志，
        #   这里关掉 df 的文件日志（log_level=ERROR 也顺带避免 INFO 噪音刷屏）。
        model, df_state, _ = init_df(log_file=None, log_level="ERROR")
        audio, sr = librosa.load(str(src), sr=48000, mono=True)
        if progress_cb:
            progress_cb(0.4)

        # 0.5 的 enhance 需要 torch.Tensor（[channels, samples]）；
        # atten_lim_db 限制最大衰减，避免把人声/音乐一起抹掉
        audio_t = torch.from_numpy(audio).float().unsqueeze(0)  # [1, N]
        enhanced_t = enhance(model, df_state, audio_t, atten_lim_db=self.atten_lim_db)
        enhanced = enhanced_t.squeeze().cpu().numpy()
        if progress_cb:
            progress_cb(0.9)

        dst.parent.mkdir(parents=True, exist_ok=True)
        sf.write(str(dst), enhanced, sr)
        if progress_cb:
            progress_cb(1.0)
        return dst
