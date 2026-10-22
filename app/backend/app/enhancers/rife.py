"""视频补帧增强器：RIFE（Real-time Intermediate Flow Estimation）帧率插值，4.26 架构。

用 RIFE 4.26（Practical-RIFE 多尺度架构，支持任意 timestep）在相邻帧之间插入中间帧，
把视频帧率翻倍（multi=2 → 30fps→60fps；multi=4 → 120fps）。运动更平滑、无顿挫。

管线（★ 2026-10-21 全面改为 GPU/管道化，不再落 PNG、不再 CPU x264 编码）：
    ffmpeg 解码 → rawvideo(BGR24) 管道 → 读帧线程预取队列
      → 相邻帧按 timestep=(k+1)/multi 用 RIFE 在 GPU 上插出中间帧
      → 直接喂给 ffmpeg 编码器（优先 NVENC 硬编码，无 NVENC 才回退 libx264）
      → 与源音频 -c:a copy 合流。
  这样 CPU 只做解码/封装，RIFE 推理 + 编码都在 GPU；进度按"已处理帧/总帧数"平滑推进，
  最后 100% 只有在编码器 flush 完（真正写完文件）时才报。

模型（backend/models/rife/，4.26，本机 RTX 5080/cu130 实测通过）：
  train_log/IFNet_HDv3.py   (4.26 多尺度架构 IFNet)
  train_log/flownet.pkl     (4.26 权重；load_state_dict 需 strict=False：含 teacher/caltime 额外键)
  model/warplayer.py        (warp 函数；已修 fp16 grid dtype 冲突）

进度回调签名同 realesrgan：progress_cb(progress, processed, total)。
"""
from __future__ import annotations

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

# RIFE 模型目录：backend/models/rife/
MODELS_DIR = Path(__file__).resolve().parent.parent.parent / "models" / "rife"
TRAIN_LOG = MODELS_DIR / "train_log"


def _find_ffmpeg() -> Optional[str]:
    """统一解析（T24：支持 FFMPEG_PATH；详见 ..ffmpeg_tools）。

    ★ 这里**不抛异常**（本值在 import 期求值，抛了会让整个增强器注册失败）；
      解析不到时返回 None，由使用的入口给出清晰错误。
    """
    return ffmpeg_tools.find_ffmpeg(required=False) or None


FFMPEG = _find_ffmpeg()

_ENCODER_CACHE: dict = {}


def _pick_encoder() -> tuple:
    """选 H.264 编码器：优先 NVENC（GPU 硬编码），否则 libx264（CPU）。"""
    return ffmpeg_tools.pick_h264_encoder(FFMPEG)


@register("rife")
class RifeEnhancer:
    """视频补帧增强器：RIFE 4.26 帧率插值。

    multi：插帧倍数（2=帧率×2，4=×4）。device：'cuda'/'cpu'。fp16：半精度加速。
    """

    def __init__(self, device: str = "cuda", multi: int = 2, fp16: bool = True,
                 scale: Optional[float] = None):
        self.device = device
        self.multi = max(2, int(multi))
        self.fp16 = fp16
        # scale：None=自动（分辨率高时用 0.5，RIFE 官方对高清建议），否则固定 0.25/0.5/1.0/2.0/4.0
        self.scale = scale
        self._model = None

    # ---- 懒加载 RIFE 模型（4.26） ----
    def _load_model(self):
        if self._model is not None:
            return self._model
        import sys

        import torch

        rife_root = str(MODELS_DIR)
        for p in [rife_root, str(TRAIN_LOG), str(MODELS_DIR / "model")]:
            if p not in sys.path:
                sys.path.insert(0, p)
        try:
            from IFNet_HDv3 import IFNet
        except ImportError as e:
            raise RuntimeError(
                "RIFE 模型架构缺失（backend/models/rife/train_log/IFNet_HDv3.py），"
                "原因为：{}".format(e)
            ) from e
        pkl = TRAIN_LOG / "flownet.pkl"
        if not pkl.exists():
            raise RuntimeError("RIFE 权重缺失：{}".format(pkl))

        # ★ device 显式解析：请求 cuda 但 CUDA 不可用时才回退 CPU，并把结论写进日志，
        #   避免"以为是 GPU、实际在跑 CPU"说不清。
        want = (self.device or "cuda").lower()
        cuda_ok = bool(getattr(torch, "cuda", None) and torch.cuda.is_available())
        if want.startswith("cuda") and not cuda_ok:
            logger.warning("RIFE：请求 GPU 但 CUDA 不可用 → 回退用 CPU 跑（会慢很多）")
        device = torch.device(want if (not want.startswith("cuda") or cuda_ok) else "cpu")

        model = IFNet()
        state = torch.load(str(pkl), map_location="cpu")
        state = {k.replace("module.", ""): v for k, v in state.items()}
        # strict=False 必需：flownet.pkl 含 teacher.*/caltime.* 子网键（被注释掉的训练结构）
        model.load_state_dict(state, strict=False)
        model.eval()
        if self.fp16 and device.type == "cuda":
            model.half()
        model.to(device)
        codec, _enc_args = _pick_encoder()
        logger.info("RIFE：推理设备=%s fp16=%s 编码器=%s（%s）",
                    device, self.fp16 and device.type == "cuda", codec,
                    "GPU 硬编码" if codec == "h264_nvenc" else "CPU x264")
        self._model = (model, device)
        return self._model

    # ---- 单帧插值：I0,I1 uint8(H,W,3,RGB) -> timestep 处中间帧 uint8 RGB ----
    def _interpolate(self, I0, I1, timestep, model, device):
        import numpy as np
        import torch
        from torch.nn import functional as F

        h, w, _ = I0.shape
        scale = self.scale or (0.5 if max(h, w) > 1080 else 1.0)  # 高清自动降采样提速
        m = max(128, int(128 / scale))
        use_half = self.fp16 and device.type == "cuda"

        def to_t(a):
            # ★ GPU 端做 uint8→float/255的转换：以前在 CPU 上 astype(float32)
            #   每帧 1080p 要多分配+转换 25MB，是 CPU 占用大户；现在只传 6MB uint8 上去
            t = torch.from_numpy(np.ascontiguousarray(a)).to(device, non_blocking=True)
            return (t.permute(2, 0, 1).unsqueeze(0).float() / 255.0)

        i0, i1 = to_t(I0), to_t(I1)
        _, _, hh, ww = i0.shape
        ph = ((hh - 1) // m + 1) * m
        pw = ((ww - 1) // m + 1) * m
        pad = (0, pw - ww, 0, ph - hh)
        i0 = F.pad(i0, pad)
        i1 = F.pad(i1, pad)
        if use_half:
            i0, i1 = i0.half(), i1.half()

        scale_list = [16 / scale, 8 / scale, 4 / scale, 2 / scale, 1 / scale]
        with torch.no_grad():
            _, _, merged = model(torch.cat((i0, i1), 1), timestep, scale_list)
            out = merged[-1][0]
        # ★ 在 GPU 上先 clamp/转 uint8，再传 6MB 回 CPU（以前是传 25MB float 回来再转）
        out8 = (out.clamp(0, 1) * 255.0).to(torch.uint8).permute(1, 2, 0).contiguous()
        arr = out8.cpu().numpy()
        return arr[:h, :w]

    def enhance(self, src: Path, dst: Path,
                progress_cb: Optional[Callable[[float], None]] = None) -> Path:
        if not FFMPEG:
            raise RuntimeError("未找到 ffmpeg（解码/合流需要）。" + ffmpeg_tools.HINT)
        if not src.exists():
            raise RuntimeError("源视频文件不存在：{}".format(src))

        import numpy as np

        w, h, fps, duration = self._probe_video_info(src)
        if w <= 0 or h <= 0:
            raise RuntimeError("读不到源视频分辨率：{}".format(src.name))
        # ★ HDR 保护：10bit PQ/HLG 先 tonemap 到 SDR 再补帧，否则颜色会发灰/爆高光
        color = ffmpeg_tools.probe_color(src, FFMPEG)
        hdr = bool(color.get("hdr"))
        dec_extra = []
        out_color_args = []
        if hdr:
            if ffmpeg_tools.has_zscale_tonemap(FFMPEG):
                dec_extra = ["-vf", ffmpeg_tools.hdr_to_sdr_filter("rgb24")]
                out_color_args = ffmpeg_tools.sdr_color_args()
                logger.warning("RIFE：源是 HDR（%s）→ 先做 HDR→SDR 色调映射再补帧，输出 SDR BT.709",
                               color.get("transfer") or "bt2020")
            else:
                logger.warning("RIFE：源是 HDR 但 ffmpeg 不带 zscale/tonemap → 直接 8bit（可能发灰）")
        est_total = int(round(duration * fps)) if duration and fps else 0
        out_fps = fps * self.multi
        frame_bytes = w * h * 3

        model, device = self._load_model()
        codec, enc_args = _pick_encoder()
        n_mid = self.multi - 1

        if progress_cb:
            progress_cb(0.0)

        # ---- 解码：ffmpeg → rawvideo(BGR24) 管道（不再写 PNG，省掉大量 CPU 与磁盘 IO）----
        dec = subprocess.Popen(
            [FFMPEG, "-v", "error", "-i", str(src)] + dec_extra + [
             "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
        dst.parent.mkdir(parents=True, exist_ok=True)
        # ---- 编码：优先 NVENC（GPU 硬编码；没有才回退 libx264 CPU）----
        enc = subprocess.Popen(
            [FFMPEG, "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
             "-s", "{}x{}".format(w, h), "-r", str(out_fps), "-i", "-",
             "-i", str(src), "-map", "0:v", "-map", "1:a?",
             "-c:v", codec] + enc_args + [
             "-pix_fmt", "yuv420p"] + out_color_args + [
             "-c:a", "copy", str(dst)],
            stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )

        # ---- 读帧线程：预取若干帧，让"解码"与"GPU 推理"并行，GPU 不用等 IO ----
        q: "queue.Queue" = queue.Queue(maxsize=12)

        def reader():
            try:
                while True:
                    buf = dec.stdout.read(frame_bytes)
                    if not buf or len(buf) < frame_bytes:
                        break
                    # copy()：frombuffer 的数组是只读的，torch.from_numpy 不接受；这里拷一次（6MB/帧，很快）
                    q.put(np.frombuffer(buf, dtype=np.uint8).reshape(h, w, 3).copy())
            except Exception:
                pass
            finally:
                q.put(None)
                try:
                    dec.stdout.close()
                except Exception:
                    pass

        threading.Thread(target=reader, name="rife-reader", daemon=True).start()

        n_pairs = 0
        n_written = 0

        def write_frame(bgr):
            nonlocal n_written
            enc.stdin.write(bgr.tobytes())
            n_written += 1

        def report(p):
            if progress_cb:
                # 只到 0.97：最后 3% 留给编码器 flush（真正写完文件才算 100%）
                progress_cb(min(0.97, max(0.0, float(p))), n_pairs, est_total or n_pairs)

        try:
            first = q.get()
            if first is None:
                raise RuntimeError("解码失败（0 帧）")
            write_frame(first)
            prev = first
            while True:
                cur = q.get()
                if cur is None:
                    break
                for k in range(n_mid):
                    ts = (k + 1) / (n_mid + 1)
                    mid_rgb = self._interpolate(prev, cur, ts, model, device)
                    write_frame(mid_rgb)
                write_frame(cur)
                prev = cur
                n_pairs += 1
                # 进度按"已处理输入帧 / 估计总帧数"平滑推进
                denom = est_total if est_total else (n_pairs + 1)
                report(min(1.0, n_pairs / max(1, denom - 1)))
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
                enc.wait(timeout=600)
            except Exception:
                try:
                    enc.kill()
                except Exception:
                    pass
        if enc.returncode not in (0, None):
            raise RuntimeError("编码失败（ffmpeg 退出码 {}）".format(enc.returncode))
        if progress_cb:
            progress_cb(1.0)
        logger.info("RIFE：完成 %s → %s（输入 %d 帧，输出 %d 帧，编码器 %s，输出 %.1f fps）",
                    Path(src).name, Path(dst).name, n_pairs + 1, n_written, codec, out_fps)
        return dst

    # ---- ffmpeg 探测 ----
    def _probe_video_info(self, src: Path) -> tuple:
        """返回 (w, h, fps, duration_s)；失败给安全默认值。"""
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
