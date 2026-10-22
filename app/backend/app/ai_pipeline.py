"""AI 增强管线：模型插槽注册 + 任务调度（P2 完善队列与进度）。

设计目标：增强器全部走「模型插槽」——统一接口 enhance(src, dst) -> dst，
FiPA-SR 官方开源后用同接口替换 audio_sr 插槽的实现，前端与调度零改动。

★ 2026-10-20：节点图支持"每个节点单独选参数"——
  ENHANCER_PARAMS 定义每个增强器可调参数（类型/默认值/可选项/范围），
  前端 /api/enhancers 拿到后渲染成节点卡上的下拉框/开关/滑条，
  提交增强链时按节点带 params，run_enhance 用 schema 校验后再实例化。
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Any, Callable, Optional

logger = logging.getLogger(__name__)

# ---- 模型插槽：enhancer 名称 -> 增强器类 ----
ENHANCERS: dict[str, type] = {}


def register(name: str):
    """装饰器：把增强器类注册进插槽（enhancers/ 目录下各模块使用）"""

    def deco(cls):
        ENHANCERS[name] = cls
        return cls

    return deco


def list_enhancers() -> list[str]:
    return sorted(ENHANCERS.keys())


# ================= ★ 各增强器可调参数（节点图 UI 直接按这个渲染）=================
# 字段：key/label/type(choice|bool|int|float)/default/choices/min/max/step/unit/help
ENHANCER_PARAMS: dict[str, list[dict]] = {
    # 响度统一：暂时没有可调项（目标固定 -14 LUFS）
    "loudness": [],
    # Hi-Res 重采样（libsoxr）
    "soxr_hires": [
        {"key": "target_sr", "label": "目标采样率", "type": "choice", "default": 96000,
         "choices": [48000, 88200, 96000, 176400, 192000], "unit": "Hz"},
        {"key": "quality", "label": "重采样品质", "type": "choice", "default": "HQ",
         "choices": ["QQ", "HQ", "VHQ"], "help": "QQ 最快 / HQ 标准 / VHQ 最精细（更慢）"},
        {"key": "bit_depth", "label": "位深", "type": "choice", "default": 24,
         "choices": [16, 24], "unit": "bit"},
    ],
    # FFT 重采样（纯 numpy）
    "fft_hires": [
        {"key": "target_sr", "label": "目标采样率", "type": "choice", "default": 96000,
         "choices": [48000, 88200, 96000, 176400, 192000], "unit": "Hz"},
        {"key": "bit_depth", "label": "位深", "type": "choice", "default": 24,
         "choices": [16, 24], "unit": "bit"},
    ],
    # AI 音频超分（LavaSR）
    "audio_sr": [
        {"key": "denoise", "label": "附带降噪", "type": "bool", "default": False,
         "help": "开：增强同时压噪声；关：只补/增强，不动噪声"},
        {"key": "batch", "label": "长音频批量模式", "type": "bool", "default": False,
         "help": "长音频开这个更省显存（分块处理）"},
    ],
    # 人声降噪（DeepFilterNet）
    "denoise": [
        {"key": "atten_lim_db", "label": "降噪强度上限", "type": "int", "default": 8,
         "min": 0, "max": 24, "step": 1, "unit": "dB",
         "help": "越小越温和（保留更多原声）/ 越大越激进（抹掉更多声音）"},
    ],
    # AI 视频超分（Real-ESRGAN）
    "realesrgan": [
        {"key": "model_name", "label": "模型", "type": "choice", "default": "RealESRGAN_x4plus",
         "choices": ["RealESRGAN_x4plus", "RealESRGAN_x4plus_anime"],
         "help": "通用（默认）/ 动漫风格更锐。注：只选 2x 时会自动切到 2x 专用模型（约 5 倍快）"},
        {"key": "scale", "label": "放大倍数", "type": "choice", "default": 2,
         "choices": [2, 3, 4], "unit": "x",
         "help": "1080p 用 2x 输出 2160p 比较稳；4x 会输出 4320p，非常吃显存/内存/CPU（已自动切块 + 内存保护）"},
    ],
    # RIFE 补帧
    "rife": [
        {"key": "multi", "label": "补帧倍数", "type": "choice", "default": 2,
         "choices": [2, 4, 8], "unit": "x", "help": "2=帧率翻倍，4=四倍，8=八倍"},
        {"key": "fp16", "label": "半精度（更快）", "type": "bool", "default": True,
         "help": "显存不足时关掉；关掉更慢但更稳"},
        {"key": "scale", "label": "输入缩放（省显存）", "type": "choice", "default": None,
         "choices": [None, 1.0, 0.5, 0.25],
         "choices_label": ["不缩放", "1.0", "0.5", "0.25"],
         "help": "4K 视频显存不够时降到 0.5 再补帧，产物分辨率会变小"},
    ],
}


def enhancer_params(key: str) -> list[dict]:
    """某增强器的可调参数 schema（没有则空列表）。"""
    return ENHANCER_PARAMS.get(key, [])


def _coerce_param(spec: dict, raw: Any) -> Any:
    """把前端传来的值按 schema 校验/转换；非法值抛 ValueError（提示要看得懂）。"""
    key = spec["key"]
    label = spec.get("label") or key
    typ = spec.get("type") or "choice"
    if typ == "bool":
        if isinstance(raw, str):
            return raw.strip().lower() in ("1", "true", "yes", "on")
        return bool(raw)
    if typ in ("int", "float"):
        try:
            v = float(raw)
        except (TypeError, ValueError):
            raise ValueError("参数「{}」需要数字，收到 {!r}".format(label, raw))
        lo, hi = spec.get("min"), spec.get("max")
        if lo is not None and v < lo:
            v = lo
        if hi is not None and v > hi:
            v = hi
        if typ == "int":
            return int(round(v))
        step = spec.get("step")
        if step:
            v = round(v / float(step)) * float(step)
        return v
    # choice：按 choices 的原类型返回
    choices = spec.get("choices") or []
    for c in choices:
        if c == raw:
            return c
        if isinstance(c, (int, float)) and not isinstance(c, bool):
            try:
                if float(raw) == float(c):
                    return c
            except (TypeError, ValueError):
                pass
        if isinstance(c, str) and isinstance(raw, str) and raw.strip() == c.strip():
            return c
    # 没匹配到：允许 None（表示"不缩放"这类选项）
    if raw is None or raw == "null" or raw == "":
        if None in choices:
            return None
    raise ValueError("参数「{}」的值 {!r} 不在可选项 {} 里".format(label, raw, choices))


def sanitize_params(key: str, params: Optional[dict]) -> dict:
    """按 schema 清洗参数：只保留认识的键、按类型转换、校验范围；未知键丢弃。"""
    specs = {s["key"]: s for s in enhancer_params(key)}
    out: dict = {}
    for k, v in (params or {}).items():
        spec = specs.get(k)
        if spec is None:
            logger.warning("增强器 %s 忽略未知参数 %s=%r", key, k, v)
            continue
        if v is None:
            if None in (spec.get("choices") or []):
                out[k] = None
            continue
        out[k] = _coerce_param(spec, v)
    return out


def run_enhance(enhancer: str, src: Path, dst: Path, device: str = "cuda",
                progress_cb: Optional[Callable[[float], None]] = None,
                params: Optional[dict] = None) -> Path:
    """执行一次增强任务（同步版；P2 由后台线程调用）。

    params：节点参数（节点图里每个节点单独选的值），先按 schema 校验再传给增强器构造函数。
    """
    cls = ENHANCERS.get(enhancer)
    if cls is None:
        raise ValueError(f"未注册的增强器: {enhancer}，可用: {list_enhancers()}")
    clean = sanitize_params(enhancer, params)
    try:
        inst = cls(device=device, **clean)
    except TypeError as e:
        raise ValueError("增强器 {} 不接受参数 {}：{}".format(enhancer, clean, e)) from e
    # 增强器如支持进度回调则透传
    return inst.enhance(src, dst, progress_cb) if progress_cb else inst.enhance(src, dst)
