"""ffmpeg / ffprobe 路径解析（全项目唯一实现）。

T24：以前 `services.py`、`bilibili.py`、`downloader.py`、`enhancers/loudness.py`、
`enhancers/realesrgan.py`、`enhancers/rife.py` **各自抄了一份** `_find_ffmpeg()`，
而且除 `downloader.py` 外都只认死路径 `D:\\ETHEREIS\\third_party\\ffmpeg\\bin\\ffmpeg.exe`
→ **换机/改路径就踩**（T19 报告里提的换机风险）。现在统一到这里。

解析优先级
    1. 环境变量 **FFMPEG_PATH**（指向 ffmpeg 可执行文件 → 取它所在目录；指向目录 → 直接用）。
       ★ 一旦设了它就是**权威**：里面没有 ffmpeg 会**明确报错**，不会偷偷回落到别的 ffmpeg
         （否则"我指了空目录却还在用老的"这种问题会被静默吞掉）。
    2. 常见安装位置探测（含历史硬编码位置，保证本机默认行为不变）。
    3. `shutil.which("ffmpeg")`（PATH）。
    4. 都没有 → 清晰报错（告诉用户设 FFMPEG_PATH 即可）。

`find_ffprobe()` 优先取 ffmpeg 同目录的 ffprobe，再退 PATH。

★ 不做缓存：解析很便宜（几次 is_file），而缓存会让"运行中改 FFMPEG_PATH"或测试失去可预测性。
"""
from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Optional

# 常见安装位置（按顺序探测）。前两项是历史硬编码位置，保留它们 = 本机默认行为不变。
COMMON_BIN_DIRS = (
    r"D:\ETHEREIS\third_party\ffmpeg\bin",
    r"C:\ffmpeg\bin",
    r"D:\DSH\VOCALIVE\ffmpeg\bin",          # 本仓库引擎侧(T14)使用的那份 ffmpeg
    r"D:\ffmpeg\bin",
    r"C:\Program Files\ffmpeg\bin",
    r"C:\Program Files (x86)\ffmpeg\bin",
)

# ★ 安装版（分发给别人）：安装目录\third_party\ffmpeg\bin 也纳入探测，且优先级最高（自带的最可靠）
_INSTALL_FFMPEG_DIR = Path(__file__).resolve().parent.parent.parent / "third_party" / "ffmpeg" / "bin"
if _INSTALL_FFMPEG_DIR.is_dir() and (_INSTALL_FFMPEG_DIR / "ffmpeg.exe").is_file():
    COMMON_BIN_DIRS = (_INSTALL_FFMPEG_DIR.as_posix(),) + COMMON_BIN_DIRS

HINT = ("请设置环境变量 FFMPEG_PATH 指向 ffmpeg 可执行文件"
        r"（如 D:\ffmpeg\bin\ffmpeg.exe）或其所在目录，或把 ffmpeg 加入 PATH。")


def _exe(stem: str) -> str:
    return stem + ".exe" if os.name == "nt" else stem


def _clean(value: str) -> str:
    return (value or "").strip().strip('"').strip("'")


def _env_dir() -> Optional[Path]:
    """FFMPEG_PATH → 目录；未设置返回 None；设了但不存在 → 抛清晰错误。"""
    raw = _clean(os.environ.get("FFMPEG_PATH", ""))
    if not raw:
        return None
    p = Path(raw)
    if p.is_file():
        return p.parent                     # 指向可执行文件 → 取所在目录
    if p.is_dir():
        return p                            # 指向目录 → 直接用
    raise RuntimeError("FFMPEG_PATH={!r} 不是已存在的文件或目录。{}".format(raw, HINT))


_ENCODER_CACHE: dict = {}


def pick_h264_encoder(ffmpeg_path: Optional[str] = None) -> tuple:
    """选 H.264 编码器：优先 NVIDIA NVENC（GPU 硬编码，CPU 几乎不占用），
    没有 NVENC 才回退 libx264。返回 (codec, 参数列表)。
    """
    key = ffmpeg_path or "default"
    if key in _ENCODER_CACHE:
        return _ENCODER_CACHE[key]
    codec, args = "libx264", ["-crf", "18", "-preset", "medium"]
    exe = ffmpeg_path
    if not exe:
        try:
            exe = find_ffmpeg(required=False)
        except Exception:
            exe = None
    try:
        if exe:
            out = subprocess.run([exe, "-hide_banner", "-encoders"],
                                 capture_output=True, text=True, timeout=20).stdout
            if "h264_nvenc" in out:
                codec = "h264_nvenc"
                # p5=平衡档，hq=高质量调优，vbr+cq=恒定质量（cq 20 ≈ x264 crf18 观感）
                args = ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "20", "-b:v", "0"]
    except Exception:
        pass
    _ENCODER_CACHE[key] = (codec, args)
    return codec, args


def find_ffmpeg(required: bool = True) -> str:
    """返回 ffmpeg 的**绝对路径**；找不到且 required=True 时抛清晰错误。

    required=False 时返回 ""（供"探测不到就自己兜底/延后报错"的调用方，例如 import 期求值的
    `realesrgan.FFMPEG`、`downloader.FFMPEG_LOCATION`，以及 services 里那些放在 try 外面的调用）。
    """
    try:
        d = _env_dir()
    except RuntimeError:
        if required:
            raise
        return ""
    if d is not None:
        exe = d / _exe("ffmpeg")
        if exe.is_file():
            return str(exe)
        if required:
            raise RuntimeError(
                "FFMPEG_PATH={!r} 里没有 {}（该变量应指向 ffmpeg 可执行文件或其所在目录）。".format(
                    _clean(os.environ.get("FFMPEG_PATH", "")), exe.name))
        return ""
    for c in COMMON_BIN_DIRS:
        exe = Path(c) / _exe("ffmpeg")
        if exe.is_file():
            return str(exe)
    w = shutil.which("ffmpeg")
    if w:
        return w
    if required:
        raise RuntimeError("找不到 ffmpeg。{}".format(HINT))
    return ""


def find_ffprobe(ffmpeg_path: Optional[str] = None, required: bool = False) -> str:
    """返回 ffprobe 的绝对路径；找不到时返回 ""（required=True 则抛清晰错误）。

    顺序：ffmpeg 同目录 → FFMPEG_PATH 目录 → PATH。
    """
    base = ffmpeg_path or find_ffmpeg(required=False)
    if base:
        p = Path(base).with_name(_exe("ffprobe"))
        if p.is_file():
            return str(p)
    try:
        d = _env_dir()
        if d is not None:
            p = d / _exe("ffprobe")
            if p.is_file():
                return str(p)
    except RuntimeError:
        pass
    w = shutil.which("ffprobe")
    if w:
        return w
    if required:
        raise RuntimeError("找不到 ffprobe（一般与 ffmpeg 同目录）。{}".format(HINT))
    return ""


def ffmpeg_version(path: Optional[str] = None) -> str:
    """`ffmpeg -version` 第一行（拿不到就返回 ""）。用于自检/日志。"""
    exe = path or find_ffmpeg(required=False)
    if not exe:
        return ""
    try:
        r = subprocess.run([exe, "-version"], capture_output=True, text=True, timeout=20)
        first = ((r.stdout or "") + (r.stderr or "")).strip().splitlines()
        return first[0] if first else ""
    except Exception:
        return ""


def describe() -> str:
    """一行自检信息：解析到的 ffmpeg/ffprobe 与版本（供日志与验证用）。"""
    ff = find_ffmpeg(required=False)
    fp = find_ffprobe(ff, required=False)
    return "ffmpeg={} | ffprobe={} | {}".format(ff or "（未找到）", fp or "（未找到）",
                                                ffmpeg_version(ff) or "（未知版本）")


# ================= ★ HDR → SDR（Real-ESRGAN / RIFE 的视频解码用）=================
# 背景：10bit HDR（PQ BT.2020 / HLG）如果直接 `-pix_fmt bgr24` 解码，
#   相当于把 PQ 当普通 gamma 截断：颜色发灰、高光爆掉、暗部死黑。
#   正确做法：zscale 先变线性光 → tonemap（hable）压高光 → 转 BT.709 SDR。
_HDR_CAP_CACHE: dict = {}


def has_zscale_tonemap(ffmpeg_path: Optional[str] = None) -> bool:
    """当前 ffmpeg 是否带 zscale + tonemap 滤镜（HDR→SDR 必需）。"""
    exe = ffmpeg_path or find_ffmpeg(required=False)
    key = exe or "none"
    if key in _HDR_CAP_CACHE:
        return _HDR_CAP_CACHE[key]
    ok = False
    try:
        out = subprocess.run([exe, "-hide_banner", "-filters"],
                             capture_output=True, text=True, timeout=20).stdout
        ok = (" zscale " in out) and (" tonemap " in out)
    except Exception:
        ok = False
    _HDR_CAP_CACHE[key] = ok
    return ok


def hdr_to_sdr_filter(out_pix_fmt: str = "bgr24") -> str:
    """标准 HDR→SDR 调色链（zscale+tonemap hable），输出 8bit SDR 的 out_pix_fmt。

    实测可用（本机 ffmpeg 带 libzimg）；若 ffmpeg 不带 zscale/tonemap 请先用 has_zscale_tonemap() 检查。
    """
    return ("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            "tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=" + out_pix_fmt)


def sdr_color_args() -> list:
    """输出 SDR 时强制标注 BT.709（避免播放器把它当 HDR 解）。"""
    return ["-color_primaries", "bt709", "-color_trc", "bt709",
            "-colorspace", "bt709", "-color_range", "tv"]


def probe_color(src, ffmpeg_path: Optional[str] = None) -> dict:
    """探测视频是不是 HDR（从 ffmpeg -i 的 Video 行里读色彩信息）。

    返回 {"hdr": bool, "transfer": str, "primaries": str, "pix_fmt": str,
              "bit_depth": int, "desc": str, "dovi": bool}
    """
    info = {"hdr": False, "transfer": "", "primaries": "", "pix_fmt": "",
            "bit_depth": 8, "desc": "", "dovi": False}
    exe = ffmpeg_path or find_ffmpeg(required=False)
    if not exe:
        return info
    try:
        err = subprocess.run([exe, "-hide_banner", "-i", str(src)],
                             capture_output=True, text=True, timeout=60).stderr
    except Exception:
        return info
    line = ""
    for ln in (err or "").splitlines():
        if "Video:" in ln:
            line = ln.strip()
            break
    if not line:
        return info
    low = line.lower()
    info["dovi"] = ("dovi" in low) or ("dolby vision" in low)
    # 10bit/12bit
    for tag, depth in (("p10le", 10), ("p12le", 12), ("p010", 10), ("p016", 16), ("yuv444p10", 10)):
        if tag in low:
            info["bit_depth"] = depth
            break
    # 色彩标记：(tv, bt2020nc/bt2020/smpte2084) 这种
    m = __import__("re").search(r"\(([^)]*)\)", line)
    meta = (m.group(1).lower() if m else "")
    for t in ("smpte2084", "arib-std-b67", "bt2020"):
        if t in meta or t in low:
            if t in ("smpte2084", "arib-std-b67"):
                info["transfer"] = t
            else:
                info["primaries"] = "bt2020"
    info["pix_fmt"] = (meta.split(",")[-2].strip() if meta.count(",") >= 2 else "")
    info["hdr"] = bool(info["transfer"] or (info["primaries"] == "bt2020" and info["bit_depth"] > 8) or info["dovi"])
    info["desc"] = line[:180]
    return info
