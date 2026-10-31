"""画质增强：Real-ESRGAN 超分辨率（腾讯开源）。

★ 2026-10-21 视频管线重写（安全版）：
    ffmpeg 解码 → rawvideo(BGR24) 管道 → 读帧线程预取（队列很小）
      → CUDA 逐帧超分（自动切块 tile，显存可控）
      → 直接喂给 ffmpeg 编码器（优先 NVENC 硬编码，无 NVENC 回退 libx264）
      → 与源音频 -c:a copy 合流。
  以前的老管线（抽帧写 PNG + 逐帧 cv2.imwrite 4x PNG + libx264 合帧）有两个大问题：
    ① 4x 放大后每帧 PNG 几十 MB，磁盘/内存/CPU 全炸（用户差点把电脑跑挂）
    ② 内存和显存没有上限，1080p 4x 整帧推理很容易 OOM
  现在：不落盘、帧队列最多 2 帧、按显存自动 tile、开始前做内存预检，OOM 给人话提示。

模型与依赖懒加载：未装 torch/realesrgan 时给出明确安装提示，
不影响后端其他功能（/api/enhancers 列表仍可用）。
"""
from __future__ import annotations

import ctypes
import logging
import queue
import re
import subprocess
import threading
from pathlib import Path
from typing import Callable, Optional

from .. import ffmpeg_tools
from ..ai_pipeline import register

logger = logging.getLogger(__name__)

# 模型目录：backend/models/（模型文件需手动下载，见下方 MODELS）
MODELS_DIR = Path(__file__).resolve().parent.parent.parent / "models"

# 模型名 -> 下载说明（Real-ESRGAN release assets）
MODELS = {
    "RealESRGAN_x4plus": {
        "file": "RealESRGAN_x4plus.pth",
        "url": "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth",
        "blocks": 23,
    },
    "RealESRGAN_x4plus_anime": {
        "file": "RealESRGAN_x4plus_anime_6B.pth",
        "url": "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth",
        "blocks": 6,
    },
    # ★ 2x 专用模型：做 2x 时直接跑 2x 网络（实测 1080p 608ms/帧），
    #   而用 4x 模型再缩到 2x 要 3083ms/帧（上下差 5 倍）。所以 scale<=2 时自动用它。
    "RealESRGAN_x2plus": {
        "file": "RealESRGAN_x2plus.pth",
        "url": "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.1/RealESRGAN_x2plus.pth",
        "blocks": 23,
        "native": 2,
    },
}

# ★ 按模型的原生放大倍数（x2plus=2，x4 系列=4）
MODEL_NATIVE = {"RealESRGAN_x2plus": 2}


def _find_ffmpeg() -> Optional[str]:
    """统一解析（T24：支持 FFMPEG_PATH；详见 ..ffmpeg_tools）。

    ★ 这里**不抛异常**（本值在 import 期求值，抛了会让整个增强器注册失败）；
      解析不到时返回 ""/None，由使用的入口给出清晰错误。
    """
    return ffmpeg_tools.find_ffmpeg(required=False) or None


FFMPEG = _find_ffmpeg()


def _run_ffmpeg(cmd: list[str]) -> None:
    """执行 ffmpeg，失败时把 stderr 的真实原因带进异常（而不是笼统的退出码）。"""
    try:
        subprocess.run(cmd, check=True, capture_output=True, text=True)
    except subprocess.CalledProcessError as e:
        lines = (e.stderr or "").strip().splitlines()
        detail = " | ".join(lines[-4:]) if lines else "exit {}".format(e.returncode)
        raise RuntimeError("ffmpeg 失败：{}".format(detail)) from e


# ---------------- ★ 内存/显存安全防护 ----------------
class _MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


def _avail_ram_gb() -> tuple:
    """返回 (可用物理内存 GB, 总物理内存 GB)；拿不到返回 (0, 0)。"""
    try:
        m = _MEMORYSTATUSEX()
        m.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):
            return m.ullAvailPhys / 1e9, m.ullTotalPhys / 1e9
    except Exception:
        pass
    return 0.0, 0.0


def _vram_free_gb() -> float:
    try:
        import torch
        if torch.cuda.is_available():
            free, _total = torch.cuda.mem_get_info()
            return free / 1e9
    except Exception:
        pass
    return 0.0


@register("realesrgan")
class RealesrganEnhancer:
    """视频/图片超分增强（画质提升，输出 scale 倍）"""

    def __init__(self, device: str = "cuda", model_name: str = "RealESRGAN_x4plus",
                 scale: int = 4):
        self.device = device
        self.model_name = model_name
        self.scale = max(1, int(scale))
        self._upsampler = None
        self._tile = 0

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        """增强 src（图片或视频）→ dst。支持进度回调 0~1。"""
        suffix = src.suffix.lower()
        if suffix in {".png", ".jpg", ".jpeg", ".bmp", ".webp"}:
            return self._enhance_image(src, dst, progress_cb)
        return self._enhance_video(src, dst, progress_cb)

    # ---- 懒加载模型（按帧大小/显存选 tile，避免整帧爆显存）----
    def _load_model(self, frame_w: int = 0, frame_h: int = 0):
        try:
            import sys
            import types

            import torch

            # 兼容补丁：torchvision 0.28 移除了 functional_tensor（basicsr 1.4.2 仍引用）
            if "torchvision.transforms.functional_tensor" not in sys.modules:
                import torchvision.transforms.functional as _F

                _ft = types.ModuleType("torchvision.transforms.functional_tensor")
                _ft.rgb_to_grayscale = _F.rgb_to_grayscale
                sys.modules["torchvision.transforms.functional_tensor"] = _ft
            from basicsr.archs.rrdbnet_arch import RRDBNet
            from realesrgan import RealESRGANer
        except ImportError as e:
            raise RuntimeError(
                "AI 依赖未安装。请先执行：\n"
                "  pip install torch torchvision --index-url https://download.pytorch.org/whl/cu130\n"
                "  pip install -r requirements-ai.txt"
            ) from e

        # ★ 自动选模型：scale<=2 时优先用 2x 专用模型（快 ~5 倍）；
        #   动漫模型只有 4x 版，保持用户选择。找不到 2x 模型就回退 4x。
        model_key = self.model_name
        if self.scale <= 2 and model_key == "RealESRGAN_x4plus":
            if (MODELS_DIR / MODELS["RealESRGAN_x2plus"]["file"]).exists():
                model_key = "RealESRGAN_x2plus"
            else:
                logger.warning("RealESRGAN：找不到 2x 专用模型，回退用 4x 模型做 2x（会慢 ~5 倍）")
        info = MODELS.get(model_key, MODELS["RealESRGAN_x4plus"])
        native = int(info.get("native") or 4)
        model_path = MODELS_DIR / info["file"]
        if not model_path.exists():
            raise RuntimeError(
                f"模型文件不存在：{model_path}\n"
                f"请下载后放到 models 目录：{info['url']}"
            )

        cuda_ok = bool(torch.cuda.is_available())
        want_cuda = str(self.device).lower().startswith("cuda")
        if want_cuda and not cuda_ok:
            logger.warning("RealESRGAN：请求 GPU 但 CUDA 不可用 → 回退 CPU（会非常慢）")
        device = torch.device("cuda" if (want_cuda and cuda_ok) else "cpu")

        # ★ 按"放大后帧大小 + 当前空闲显存"自动选 tile：tile=0 表示整帧（只在小图用）
        self._tile = self._pick_tile(frame_w * self.scale, frame_h * self.scale, native) if frame_w and frame_h else 0
        try:
            torch.backends.cudnn.benchmark = True
        except Exception:
            pass
        if cuda_ok:
            try:
                torch.cuda.empty_cache()
            except Exception:
                pass
        model = RRDBNet(num_in_ch=3, num_out_ch=3, num_feat=64,
                        num_block=info["blocks"], num_grow_ch=32, scale=native)
        half = bool(cuda_ok and device.type == "cuda")
        upsampler = RealESRGANer(
            scale=native, model_path=str(model_path), model=model,
            tile=self._tile, tile_pad=10, pre_pad=0, half=half,
            device=device,
        )
        try:
            codec, _args = ffmpeg_tools.pick_h264_encoder(FFMPEG)
        except Exception:
            codec = "libx264"
        logger.info("RealESRGAN：推理设备=%s fp16=%s 模型=%s（%dx 原生）tile=%s 编码器=%s",
                    device, half, model_key, native,
                    self._tile if self._tile else "整帧", codec)
        self._upsampler = upsampler
        return upsampler

    def _pick_tile(self, out_w: int, out_h: int, native: int = 4) -> int:
        """确定 tile 大小：小图整帧；大图按空闲显存切块（0=整帧）。

        ★ 实测（1080p扩到 2x，RTX 5080）：tile 128=4314ms、256=1310ms、384=600ms、512=608~637ms、768=572ms；
          法则：2x 模型用 768 最快（4x 模型用 512，1024 反而 5.3s 且占 12.5GB）。
        """
        px = max(1, out_w * out_h)
        free = _vram_free_gb()
        # 输出分辨率较大时一律切块，避免一次性把整帧特征图塞进显存
        if px <= 768 * 768:
            return 0
        best = 768 if native <= 2 else 512
        if free >= 6:
            return best
        if free >= 4:
            return 384
        if free >= 2.5:
            return 256
        return 128

    def _memory_guard(self, w: int, h: int) -> tuple:
        """开始前预检：算放大后每帧大小，内存不够就提前拒绝（而不是跑一半把系统拖死）。"""
        out_w, out_h = w * self.scale, h * self.scale
        frame_mb = out_w * out_h * 3 / 1048576.0
        ram_avail, ram_total = _avail_ram_gb()
        # 估算：输入帧 + 增强输出 + 管道/转换缓冲 ≈ 3 帧 + 模型/其它 0.8GB
        need_gb = frame_mb * 3 / 1024.0 + 0.8
        out_px = out_w * out_h
        # ★ 分辨率保护：8K 以上直接拒绝（4K 源 4x 会到 15360x8640）
        if out_px > 7680 * 4320:
            raise RuntimeError(
                "分辨率保护：输出 {}x{}（{:.1f} MP）超过 8K，AI 超分耗时不可接受，已拒绝。\n"
                "请把「放大倍数」调小（如 2x），或先把源视频降分辨率后再超分。".format(
                    out_w, out_h, out_px / 1e6))
        if out_px > 3840 * 2160:
            # 按实测约 12 MP/s 的有效速度估算
            logger.warning("RealESRGAN：输出 %dx%d（%.1f MP）非常大，"
                           "预估约 %.1f 秒/帧，整片会很久；建议 1080p 源用 2x。",
                           out_w, out_h, out_px / 1e6, out_px / 12e6)
        if ram_avail > 0 and need_gb > ram_avail * 0.75:
            raise RuntimeError(
                "内存保护：放大后每帧约 {:.0f} MB（{}x{}），预计需要约 {:.1f} GB 空闲内存，"
                "当前只有 {:.1f} GB。\n请把「放大倍数」调小（比如 2x）或关闭其它占内存的程序后重试。".format(
                    frame_mb, out_w, out_h, need_gb, ram_avail)
            )
        if ram_avail > 0 and ram_total > 0 and need_gb > ram_total * 0.5:
            logger.warning("RealESRGAN：预计内存占用 %.1fGB / 总 %.1fGB，已自动切块降低峰值", need_gb, ram_total)
        return out_w, out_h

    def _enhance_image(self, src: Path, dst: Path, progress_cb) -> Path:
        if not src.exists():
            raise RuntimeError("源图片不存在：{}".format(src))
        upsampler = self._load_model(2048, 2048)
        import cv2

        img = cv2.imread(str(src), cv2.IMREAD_UNCHANGED)
        if img is None:
            raise RuntimeError("读图片失败：{}".format(src))
        h, w = img.shape[:2]
        self._memory_guard(w, h)
        try:
            import torch as _torch
            with _torch.inference_mode():
                output, _ = upsampler.enhance(img, outscale=self.scale)
        except Exception as e:
            if "out of memory" in str(e).lower() or "OutOfMemory" in type(e).__name__:
                raise RuntimeError("显存不足：请把「放大倍数」调小后重试。") from e
            raise
        dst.parent.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(dst), output)
        if progress_cb:
            progress_cb(1.0)
        return dst

    def enhance_image_bytes(self, data: bytes, out_scale: Optional[int] = None) -> bytes:
        """PDF/图片页超分：输入图片字节，直接返回 PNG 字节（不落盘）。

        只接受 8bit/24bit 图片；PDF 页面由前端渲染成 PNG 后传入。
        """
        import cv2
        import numpy as np

        arr = np.frombuffer(data, np.uint8)
        img = cv2.imdecode(arr, cv2.IMREAD_UNCHANGED)
        if img is None:
            raise RuntimeError("超分失败：无法解码页面图片")
        if img.ndim != 3 or img.shape[2] not in (3, 4):
            raise RuntimeError("超分失败：页面图片不是 RGB/BGRA")
        if img.shape[2] == 4:
            img = cv2.cvtColor(img, cv2.COLOR_BGRA2BGR)
        h, w = img.shape[:2]
        if w * h > 40_000_000:
            raise RuntimeError("超分失败：页面图片过大（{}x{}），请减小渲染宽度".format(w, h))
        scale = int(out_scale or self.scale)
        if scale not in (2, 4):
            raise RuntimeError("超分倍率只支持 2 或 4")
        self.scale = scale
        self._memory_guard(w, h)
        upsampler = self._load_model(w, h)
        try:
            import torch as _torch
            with _torch.inference_mode():
                output, _ = upsampler.enhance(img, outscale=scale)
        except Exception as e:
            if "out of memory" in str(e).lower() or "OutOfMemory" in type(e).__name__:
                raise RuntimeError("显存不足：请把「倍率」调小后重试。") from e
            raise
        ok, enc = cv2.imencode(".png", output)
        if not ok:
            raise RuntimeError("超分失败：PNG 编码失败")
        return enc.tobytes()

    # ---- 视频：流式管线（不落盘 + 切块 + NVENC）----
    def _enhance_video(self, src: Path, dst: Path, progress_cb) -> Path:
        if not FFMPEG:
            raise RuntimeError("未找到 ffmpeg（解码/合流需要）。" + ffmpeg_tools.HINT)
        if not src.exists():
            raise RuntimeError("源视频文件不存在：{}".format(src))

        import numpy as np

        w, h, fps, duration = self._probe_video_info(src)
        if w <= 0 or h <= 0:
            raise RuntimeError("读不到源视频分辨率：{}".format(src.name))
        out_w, out_h = self._memory_guard(w, h)

        # ★ HDR 保护：10bit PQ/HLG 直接当 8bit 解码会发灰/爆高光，
        #   用 zscale + tonemap(hable) 正确压高光到 SDR，输出标注 BT.709。
        color = ffmpeg_tools.probe_color(src, FFMPEG)
        hdr = bool(color.get("hdr"))
        dec_extra = []
        out_color_args = []
        if hdr:
            if ffmpeg_tools.has_zscale_tonemap(FFMPEG):
                dec_extra = ["-vf", ffmpeg_tools.hdr_to_sdr_filter("bgr24")]
                out_color_args = ffmpeg_tools.sdr_color_args()
                logger.warning("RealESRGAN：源是 HDR（%s/%s %dbit%s）→ 解码时做 HDR→SDR 色调映射"
                               "（zscale+tonemap hable）；输出为 SDR BT.709，HDR 高光会被压缩",
                               color.get("transfer") or "bt2020", color.get("primaries") or "?",
                               color.get("bit_depth"), "（杜比视界，可能偏色）" if color.get("dovi") else "")
            else:
                logger.warning("RealESRGAN：源是 HDR 但 ffmpeg 不带 zscale/tonemap → 直接 8bit 解码"
                               "（色彩可能发灰/爆高光）")
        est_total = int(round(duration * fps)) if duration and fps else 0

        upsampler = self._load_model(w, h)
        try:
            codec, enc_args = ffmpeg_tools.pick_h264_encoder(FFMPEG)
        except Exception:
            codec, enc_args = "libx264", ["-crf", "18", "-preset", "medium"]

        frame_bytes = w * h * 3
        if progress_cb:
            progress_cb(0.0, 0, est_total)

        # 解码：ffmpeg → rawvideo(BGR24) 管道（不写 PNG，省掉巨量磁盘/内存）
        dec = subprocess.Popen(
            [FFMPEG, "-v", "error", "-i", str(src)] + dec_extra + [
             "-f", "rawvideo", "-pix_fmt", "bgr24", "-"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
        dst.parent.mkdir(parents=True, exist_ok=True)
        # 编码：rawvideo 管道 → 优先 NVENC（GPU 硬编码）→ 原音频 copy
        enc = subprocess.Popen(
            [FFMPEG, "-y", "-f", "rawvideo", "-pix_fmt", "bgr24",
             "-s", "{}x{}".format(out_w, out_h), "-r", str(fps), "-i", "-",
             "-i", str(src), "-map", "0:v", "-map", "1:a?",
             "-c:v", codec] + enc_args + [
             "-pix_fmt", "yuv420p"] + out_color_args + [
             "-c:a", "copy", "-shortest", str(dst)],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )

        # 读帧线程：最多预取 2 帧（★ 安全防护：放大后一帧几十 MB，队列不能大）
        q: "queue.Queue" = queue.Queue(maxsize=2)

        def reader():
            try:
                while True:
                    buf = dec.stdout.read(frame_bytes)
                    if not buf or len(buf) < frame_bytes:
                        break
                    q.put(np.frombuffer(buf, dtype=np.uint8).reshape(h, w, 3))
            except Exception:
                pass
            finally:
                q.put(None)
                try:
                    dec.stdout.close()
                except Exception:
                    pass

        threading.Thread(target=reader, name="esrgan-reader", daemon=True).start()

        n_in = 0
        n_out = 0

        def write_out(bgr):
            nonlocal n_out
            enc.stdin.write(bgr.tobytes())
            n_out += 1

        try:
            while True:
                frame = q.get()
                if frame is None:
                    break
                try:
                    import torch as _torch
                    with _torch.inference_mode():
                        output, _ = upsampler.enhance(frame, outscale=self.scale)
                except Exception as e:
                    msg = str(e).lower()
                    if "out of memory" in msg or "OutOfMemory" in type(e).__name__:
                        try:
                            import torch
                            torch.cuda.empty_cache()
                        except Exception:
                            pass
                        raise RuntimeError(
                            "显存不足（RealESRGAN）：请把「放大倍数」调小（如 2x），"
                            "或关闭其它占用 GPU 的程序后重试。剩余显存约 {:.1f} GB。".format(
                                _vram_free_gb())
                        ) from e
                    raise
                if output is None:
                    raise RuntimeError("RealESRGAN 没有输出帧")
                out_h2, out_w2 = output.shape[:2]
                if out_w2 != out_w or out_h2 != out_h:
                    out_w, out_h = out_w2, out_h2
                write_out(output)
                n_in += 1
                if progress_cb:
                    total = est_total if est_total else (n_in + 1)
                    progress_cb(min(0.97, 0.02 + 0.95 * n_in / max(1, total)), n_in, est_total or n_in)
        finally:
            try:
                enc.stdin.close()
            except Exception:
                pass
            try:
                dec.kill()
            except Exception:
                pass
            try:
                dec.wait(timeout=10)
            except Exception:
                pass
            try:
                enc.wait(timeout=1800)
            except Exception:
                try:
                    enc.kill()
                except Exception:
                    pass
            try:
                import torch
                torch.cuda.empty_cache()
            except Exception:
                pass
        if enc.returncode not in (0, None):
            raise RuntimeError("编码失败（ffmpeg 退出码 {}）".format(enc.returncode))
        if progress_cb:
            progress_cb(1.0)
        logger.info("RealESRGAN：完成 %s → %s（输入 %d 帧，输出 %d 帧，%dx%d，编码器 %s）",
                    Path(src).name, Path(dst).name, n_in, n_out, out_w, out_h, codec)
        return dst

    # ---- ffmpeg 探测 ----
    def _probe_video_info(self, src: Path) -> tuple:
        """返回 (w, h, fps, duration_s)。"""
        w = h = 0
        fps = 30.0
        duration = 0.0
        try:
            err = subprocess.run([FFMPEG, "-hide_banner", "-i", str(src)],
                                 capture_output=True, text=True, timeout=60).stderr
            m = re.search(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", err)
            if m:
                duration = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
            mv = re.search(r"Video:.*?,\s*(\d{2,5})x(\d{2,5})", err)
            if mv:
                w, h = int(mv.group(1)), int(mv.group(2))
            mf = re.search(r"(\d+(?:\.\d+)?)\s*fps", err)
            if mf:
                fps = float(mf.group(1))
        except Exception:
            pass
        return w, h, fps, duration
