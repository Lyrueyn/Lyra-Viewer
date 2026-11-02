"""AI 字幕转写子进程。

为什么单独开进程：
  Whisper 模型 + PyTorch + CUDA 上下文会长期占用 1GB+ 内存和显存。后端是常驻
  HTTP 服务，不能因为字幕任务结束就退出；把转写放在一次性的子进程里，任务完成后
  子进程退出，模型、CUDA 上下文、显存全部归还系统，后端本体只保留很轻的状态。

父进程通过 --status 指定的 JSON 文件轮询进度；取消时父进程直接终止本进程。
"""
from __future__ import annotations

import argparse
import gc
import json
import os
import sys
import time
import wave
from pathlib import Path


def _write_status(path: Path, data: dict) -> None:
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(path.suffix + ".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, path)
    except Exception:
        pass


def _fmt_ts(sec: float) -> str:
    sec = max(0.0, float(sec))
    h = int(sec // 3600)
    m = int((sec % 3600) // 60)
    s = sec % 60
    return "%02d:%02d:%06.3f" % (h, m, s)


def _seg_get(seg, key: str, default=0):
    if isinstance(seg, dict):
        return seg.get(key, default)
    return getattr(seg, key, default)


def _to_simplified(text: str, lang: str = "") -> str:
    try:
        if not text or not any("\u4e00" <= ch <= "\u9fff" for ch in text):
            return text
        import zhconv
        return zhconv.convert(text, "zh-hans")
    except Exception:
        return text


def _transcribe_with_progress(model, audio, *, device: str, base_sec: float,
                              span_sec: float, total_sec: float, on_progress):
    """继续使用父进程版本的 tqdm 钩子，但回调改成写状态文件。"""
    import importlib
    wt = importlib.import_module("whisper.transcribe")
    original_tqdm = wt.tqdm.tqdm
    last_emit = [0.0]

    class _ProgressTqdm:
        def __init__(self, *args, **kwargs):
            self.total = int(kwargs.get("total") or (args[0] if args else 0) or 0)
            self.n = 0

        def __enter__(self):
            return self

        def __exit__(self, *args):
            return False

        def update(self, n=1):
            try:
                self.n += int(n or 0)
                frac = min(1.0, max(0.0, self.n / max(1, self.total)))
                pct = (base_sec + span_sec * frac) / max(0.01, total_sec) * 100.0
                now = time.time()
                if now - last_emit[0] >= 0.25:
                    last_emit[0] = now
                    on_progress(round(min(99.0, max(1.0, pct)), 1))
            except Exception:
                pass

        def close(self):
            pass

        def set_description(self, *args, **kwargs):
            pass

        def set_postfix(self, *args, **kwargs):
            pass

        def __getattr__(self, _name):
            return lambda *args, **kwargs: None

    wt.tqdm.tqdm = _ProgressTqdm
    try:
        return model.transcribe(audio, language=None, task="transcribe",
                                fp16=(device == "cuda"), verbose=False,
                                condition_on_previous_text=False)
    finally:
        wt.tqdm.tqdm = original_tqdm


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--wav", required=True)
    ap.add_argument("--srt", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--model-dir", required=True)
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--total-sec", type=float, default=0.0)
    ap.add_argument("--status", required=True)
    args = ap.parse_args()

    wav = Path(args.wav)
    srt_path = Path(args.srt)
    status_path = Path(args.status)
    model = None
    state = {"percent": 1.0, "stage": "加载模型", "segments": 0, "language": "",
             "done": False, "ok": False, "error": "", "srt": ""}
    _write_status(status_path, state)

    try:
        import numpy as np
        import torch
        import whisper

        device = args.device
        if device == "cuda" and not torch.cuda.is_available():
            device = "cpu"
        model = whisper.load_model(args.model, download_root=args.model_dir, device=device)
        state.update({"percent": 2.0, "stage": "识别中"})
        _write_status(status_path, state)

        with wave.open(str(wav), "rb") as wf:
            frames = wf.readframes(wf.getnframes())
        audio = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
        total_samples = max(1, len(audio))
        sr = 16000
        chunk_samples = sr * 60
        total_sec = float(args.total_sec) if args.total_sec and args.total_sec > 0 else total_samples / float(sr)
        segs = []
        lang = ""

        def write_srt() -> None:
            try:
                with open(srt_path, "w", encoding="utf-8", newline="") as f:
                    for i, s in enumerate(segs, 1):
                        f.write("%d\n%s --> %s\n%s\n\n" % (
                            i, _fmt_ts(s["start"]), _fmt_ts(s["end"]), s["text"]))
            except Exception:
                pass

        def on_progress(pct: float) -> None:
            state.update({"percent": pct, "stage": "识别中", "segments": len(segs),
                          "language": lang})
            _write_status(status_path, state)

        for off in range(0, total_samples, chunk_samples):
            if os.path.exists(status_path.with_suffix(".cancel")):
                raise RuntimeError("已取消")
            part = audio[off:off + chunk_samples]
            if len(part) == 0:
                break
            base_sec = off / float(sr)
            res = _transcribe_with_progress(
                model, part, device=device, base_sec=base_sec,
                span_sec=len(part) / float(sr), total_sec=total_sec, on_progress=on_progress)
            if isinstance(res, dict):
                lang = (res.get("language") or lang) or ""
                seg_list = res.get("segments") or []
            else:
                seg_list = res
            for s in seg_list:
                st = float(_seg_get(s, "start", 0) or 0) + base_sec
                en = float(_seg_get(s, "end", 0) or 0) + base_sec
                tx = (_seg_get(s, "text", "") or "").strip()
                if tx:
                    segs.append({"start": st, "end": en, "text": _to_simplified(tx, lang)})
            state.update({"segments": len(segs), "language": lang})
            write_srt()
        write_srt()
        state.update({"percent": 100.0, "stage": "完成", "done": True, "ok": True,
                      "segments": len(segs), "language": lang, "srt": str(srt_path)})
        _write_status(status_path, state)
        return 0
    except Exception as e:
        state.update({"done": True, "ok": False, "error": str(e)[:500]})
        _write_status(status_path, state)
        return 1
    finally:
        model = None
        try:
            gc.collect()
        except Exception:
            pass
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.synchronize()
                torch.cuda.empty_cache()
                torch.cuda.ipc_collect()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
