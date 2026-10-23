"""AI 字幕（Whisper 语音识别）：依赖安装、模型下载/选择/删除、离线转写生成 SRT。

设计目标（用户要求）：
· 依赖和模型都走「下载按钮 + 进度条（x/x MB）+ 取消按钮」，不要求用户手动 pip；
· 模型可以选（tiny/base/small/medium/large-v3），可以删除；
· 选择使用哪个模型 → 转写当前视频音频 → 生成 <视频名>.ai.srt（字幕文件）。
全部状态用模块级 dict + 锁，HTTP 轮询 /api/ai_subtitle/status 获取。
"""
from __future__ import annotations

import json
import logging
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
import wave
from pathlib import Path

from . import ffmpeg_tools

logger = logging.getLogger(__name__)

DATA_DIR = Path(os.environ.get("LMP_DATA_DIR") or (Path(__file__).resolve().parents[2] / "data"))
MODEL_DIR = DATA_DIR / "whisper_models"
CFG_FILE = DATA_DIR / "ai_subtitle.json"
DEPS_LOG = DATA_DIR / "whisper_deps.log"

PYTHON = sys.executable

# name, 中文标签, 约大小 MB（官方 .pt 文件）
MODELS = [
    ("tiny", "Tiny · 最快（约 76 MB）", 76),
    ("base", "Base · 快（约 142 MB）", 142),
    ("small", "Small · 均衡（约 466 MB，推荐）", 466),
    ("medium", "Medium · 更准（约 1.5 GB）", 1500),
    ("large-v3", "Large v3 · 最准（约 3 GB）", 3000),
]
MODEL_NAMES = [m[0] for m in MODELS]
MODEL_LABELS = {m[0]: m[1] for m in MODELS}
MODEL_SIZES = {m[0]: m[2] for m in MODELS}

_lock = threading.RLock()
_deps_ready = {"ts": 0.0, "ready": False}
_dep_job = {"running": False, "done": False, "ok": False, "percent": 0.0,
            "stage": "", "logs": [], "error": "", "cancel_requested": False, "started_at": 0.0}
_dl_job = {"running": False, "done": False, "ok": False, "model": "", "downloaded": 0,
           "total": 0, "percent": 0.0, "error": "", "cancel_requested": False, "started_at": 0.0}
_gen_job = {"running": False, "done": False, "ok": False, "media": "", "srt": "",
            "percent": 0.0, "segments": 0, "language": "", "error": "",
            "cancel_requested": False, "started_at": 0.0}
_cancel_deps = threading.Event()
_cancel_dl = threading.Event()
_cancel_gen = threading.Event()


def _save_cfg(cfg: dict) -> None:
    try:
        CFG_FILE.parent.mkdir(parents=True, exist_ok=True)
        CFG_FILE.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception as e:
        logger.warning("ai_subtitle 保存配置失败: %s", e)


def _load_cfg() -> dict:
    try:
        if CFG_FILE.exists():
            return json.loads(CFG_FILE.read_text(encoding="utf-8"))
    except Exception:
        pass
    return {"model": "small"}


def selected_model() -> str:
    m = str(_load_cfg().get("model") or "").strip()
    return m if m in MODEL_NAMES else ""


def select_model(name: str) -> dict:
    if name not in MODEL_NAMES:
        return {"ok": False, "error": "未知模型"}
    cfg = _load_cfg()
    cfg["model"] = name
    _save_cfg(cfg)
    logger.info("AI 字幕选择模型: %s", name)
    return {"ok": True, "model": name}


def _model_path(name: str) -> Path:
    return MODEL_DIR / (name + ".pt")


def deps_ready(force: bool = False) -> bool:
    now = time.time()
    with _lock:
        if not force and _deps_ready["ts"] and now - _deps_ready["ts"] < 300:
            return _deps_ready["ready"]
    import importlib.util
    ready = importlib.util.find_spec("whisper") is not None
    with _lock:
        _deps_ready["ts"] = now
        _deps_ready["ready"] = ready
    return ready


def models_status() -> list:
    out = []
    sel = selected_model()
    for name, label, size in MODELS:
        p = _model_path(name)
        try:
            have = p.exists() and p.stat().st_size > 1024 * 1024
            real_mb = round(p.stat().st_size / 1048576.0, 1) if have else 0
        except Exception:
            have, real_mb = False, 0
        out.append({"name": name, "label": label, "size_mb": size,
                    "downloaded": have, "file_mb": real_mb, "selected": name == sel})
    return out


_DEV_CACHE = {"ts": 0.0, "info": None}


def _device_info() -> dict:
    """返回识别设备信息。

    ★ 不要 import torch：AI 字幕设置页会定时查 status，一 import torch 后端常驻内存会多几百 MB，
      即使你根本没打开 AI 字幕。这里用 nvidia-smi（只读命令行）探测显卡，结果缓存 5 分钟。
    """
    import time as _time
    now = _time.time()
    with _lock:
        info = _DEV_CACHE.get("info")
        if info and now - float(_DEV_CACHE.get("ts") or 0) < 300:
            return dict(info)
    force = (os.environ.get("LMP_WHISPER_DEVICE") or "").strip().lower()
    if force == "cpu":
        info = {"device": "cpu", "gpu": False, "name": "CPU（环境变量强制）",
                "torch": "", "reason": "LMP_WHISPER_DEVICE=cpu"}
    else:
        name, reason = "", ""
        try:
            r = subprocess.run(["nvidia-smi", "--query-gpu=name", "--format=csv,noheader"],
                               capture_output=True, text=True, encoding="utf-8",
                               errors="replace", timeout=5)
            if r.returncode == 0 and (r.stdout or "").strip():
                name = (r.stdout or "").strip().splitlines()[0].strip()
        except Exception as e:
            reason = "未检测到 nvidia-smi（没有 NVIDIA 驱动或不在 PATH）: " + str(e)[:80]
        if name:
            info = {"device": "cuda", "gpu": True, "name": name, "torch": ""}
        else:
            info = {"device": "cpu", "gpu": False, "name": "CPU",
                    "torch": "", "reason": reason or "未检测到可用 NVIDIA 显卡"}
    with _lock:
        _DEV_CACHE["ts"] = now
        _DEV_CACHE["info"] = info
    return dict(info)


def _ensure_zhconv() -> bool:
    """确保 zhconv 可用（中文繁体→简体）；缺了就临时 pip 安装（很小）。"""
    try:
        import zhconv  # noqa: F401
        return True
    except Exception:
        pass
    try:
        subprocess.run([PYTHON, "-m", "pip", "install", "--no-warn-script-location", "zhconv"],
                       capture_output=True, timeout=180)
    except Exception as e:
        logger.warning("安装 zhconv 失败（将不转简体）: %s", e)
    try:
        import zhconv  # noqa: F401
        return True
    except Exception:
        return False


def _to_simplified(text: str, lang: str = "") -> str:
    """把中文（尤其 large-v3 常见的繁体）统一转成简体；非中文内容原样返回。"""
    try:
        if not text:
            return text
        has_han = any("\u4e00" <= ch <= "\u9fff" for ch in text)
        if not has_han:
            return text
        if lang and not str(lang).lower().startswith("zh") and not any(
                "\u4e00" <= ch <= "\u9fff" for ch in text):
            return text
        import zhconv
        return zhconv.convert(text, "zh-hans")
    except Exception:
        return text


def status() -> dict:
    with _lock:
        dep = dict(_dep_job)
        dep["logs"] = dep.get("logs", [])[-8:]
        return {
            "ok": True,
            "deps_ready": deps_ready(),
            "selected": selected_model(),
            "models": models_status(),
            "deps_job": dep,
            "download_job": dict(_dl_job),
            "generate_job": dict(_gen_job),
            "device_info": _device_info(),
            "model_dir": str(MODEL_DIR),
        }


# ---------------------------------------------------------------- 依赖安装
def _dep_worker() -> None:
    with _lock:
        _dep_job.update({"running": True, "done": False, "ok": False, "percent": 1.0,
                         "stage": "安装语音识别依赖（openai-whisper）", "logs": [],
                         "error": "", "cancel_requested": False, "started_at": time.time()})
    _cancel_deps.clear()

    def run_pip(args: list) -> tuple:
        cmd = [PYTHON, "-m", "pip"] + args
        logger.info("AI 字幕依赖安装: %s", " ".join(cmd))
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace", bufsize=1)
        tail = []
        while True:
            if _cancel_deps.is_set():
                try:
                    proc.terminate()
                except Exception:
                    pass
                raise RuntimeError("已取消")
            line = proc.stdout.readline() if proc.stdout else ""
            if not line:
                if proc.poll() is not None:
                    break
                time.sleep(0.05)
                continue
            line = line.rstrip()
            if line:
                tail.append(line)
                tail = tail[-10:]
                with _lock:
                    _dep_job["logs"] = tail[-8:]
                    _dep_job["percent"] = round(min(95.0, _dep_job["percent"] + 0.8), 1)
        return proc.wait(), tail

    try:
        # ① 主包：--no-deps（避免 pip 去重装/解析 torch 这类大依赖；本机已有 torch）
        rc, tail = run_pip(["install", "--no-warn-script-location", "--no-deps", "openai-whisper"])
        if rc != 0:
            raise RuntimeError("安装 openai-whisper 失败（最后输出：%s）" % (" | ".join(tail[-3:])[:300]))
        # ② 小依赖：tiktoken（分词）+ numba（whisper 时间戳）+ 其它
        rc, tail = run_pip(["install", "--no-warn-script-location",
                            "tiktoken", "more-itertools", "tqdm", "regex", "numba", "zhconv"])
        if rc != 0:
            raise RuntimeError("安装 whisper 依赖包失败（最后输出：%s）" % (" | ".join(tail[-3:])[:300]))
        # ②-b torch 兜底：没有 GPU / 没装过 AI 增强的机器，单独用 AI 字幕也要能跑（CPU 版）
        try:
            import torch  # noqa: F401
        except Exception:
            logger.info("未检测到 torch，安装 CPU 版 PyTorch（无 NVIDIA 显卡也能用 AI 字幕，只是慢）")
            rc, tail = run_pip(["install", "--no-warn-script-location", "torch"])
            if rc != 0:
                raise RuntimeError("安装 PyTorch 失败（最后输出：%s）" % (" | ".join(tail[-3:])[:300]))
        _ensure_zhconv()
        deps_ready(force=True)
        if not deps_ready(force=True):
            # 回退：完整安装一次（含依赖解析）
            rc, tail = run_pip(["install", "--no-warn-script-location", "openai-whisper"])
            if rc != 0:
                raise RuntimeError("安装 openai-whisper 失败（最后输出：%s）" % (" | ".join(tail[-3:])[:300]))
            deps_ready(force=True)
        if not deps_ready(force=True):
            raise RuntimeError("安装命令完成但仍无法 import whisper（详见日志）")
        DEPS_LOG.write_text("\n".join(tail), encoding="utf-8")
        with _lock:
            _dep_job.update({"ok": True, "done": True, "running": False, "percent": 100.0,
                             "stage": "完成"})
        logger.info("AI 字幕依赖安装完成")
    except Exception as e:
        with _lock:
            _dep_job.update({"ok": False, "done": True, "running": False,
                             "error": str(e)[:300], "stage": "失败"})
        logger.warning("AI 字幕依赖安装失败: %s", e)
    finally:
        deps_ready(force=True)


def start_deps_install() -> dict:
    with _lock:
        if _dep_job["running"]:
            return {"ok": False, "error": "依赖正在安装中"}
    threading.Thread(target=_dep_worker, daemon=True, name="whisper-deps").start()
    return {"ok": True, "started": True}


def cancel_deps_install() -> dict:
    _cancel_deps.set()
    return {"ok": True}


# ---------------------------------------------------------------- 模型下载
def _dl_worker(name: str) -> None:
    _cancel_dl.clear()
    part = _model_path(name).with_suffix(".pt.part")
    try:
        try:
            import whisper  # noqa: F401
            from whisper import _MODELS as WHISPER_MODELS
        except Exception as e:
            raise RuntimeError("缺少语音识别依赖（openai-whisper）：%s" % e)
        meta = WHISPER_MODELS.get(name)
        if not meta:
            raise RuntimeError("找不到模型下载地址：%s" % name)
        if isinstance(meta, str):          # 新版 whisper：_MODELS[name] 直接是 URL 字符串
            url, sha = meta, ""
        else:
            url = meta.get("url") or ""
            sha = (meta.get("sha256") or "").lower()
        if not url:
            raise RuntimeError("找不到模型下载地址：%s" % name)
        # ★ openaipublic.azureedge.net 在本机网络下会被代理拦截（证书域名不匹配/返回 HTML），
        #   同一个存储账号的 blob.core.windows.net 可直连 → 换主机名。
        url = url.replace("openaipublic.azureedge.net", "openaipublic.blob.core.windows.net")
        MODEL_DIR.mkdir(parents=True, exist_ok=True)
        with _lock:
            _dl_job.update({"running": True, "done": False, "ok": False, "model": name,
                            "downloaded": 0, "total": 0, "percent": 0.0, "error": "",
                            "cancel_requested": False, "started_at": time.time()})
        req = urllib.request.Request(url, headers={"User-Agent": "LocalMediaPlayer/1.0"})
        with urllib.request.urlopen(req, timeout=60) as r:
            total = int(r.headers.get("Content-Length") or 0)
            with _lock:
                _dl_job["total"] = total
            done = 0
            h = None
            if sha:
                import hashlib
                h = hashlib.sha256()
            with open(part, "wb") as f:
                while True:
                    if _cancel_dl.is_set():
                        raise RuntimeError("已取消")
                    chunk = r.read(1024 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
                    if h is not None:
                        h.update(chunk)
                    done += len(chunk)
                    with _lock:
                        _dl_job["downloaded"] = done
                        _dl_job["percent"] = round((done / total * 100.0) if total else 0.0, 1)
        if sha and h and h.hexdigest().lower() != sha:
            raise RuntimeError("模型校验失败（sha256 不匹配），请重试")
        _model_path(name).unlink(missing_ok=True)
        part.replace(_model_path(name))
        with _lock:
            _dl_job.update({"ok": True, "done": True, "running": False, "percent": 100.0})
        logger.info("AI 字幕模型下载完成: %s（%.1f MB）", name, _dl_job["downloaded"] / 1048576.0)
    except Exception as e:
        try:
            part.unlink(missing_ok=True)
        except Exception:
            pass
        with _lock:
            _dl_job.update({"ok": False, "done": True, "running": False, "error": str(e)[:300]})
        logger.warning("AI 字幕模型下载失败 %s: %s", name, e)


def start_download(name: str) -> dict:
    if name not in MODEL_NAMES:
        return {"ok": False, "error": "未知模型"}
    if not deps_ready(force=True):
        return {"ok": False, "error": "还没有安装语音识别依赖（openai-whisper），请先点「下载语音识别依赖」", "need_deps": True}
    with _lock:
        if _dl_job["running"]:
            return {"ok": False, "error": "已有模型正在下载中"}
        _dl_job.update({"running": True, "done": False, "ok": False, "model": name,
                        "downloaded": 0, "total": 0, "percent": 0.0, "error": "",
                        "cancel_requested": False, "started_at": time.time()})
    threading.Thread(target=_dl_worker, args=(name,), daemon=True, name="whisper-dl").start()
    return {"ok": True, "started": True, "model": name}


def cancel_download() -> dict:
    _cancel_dl.set()
    with _lock:
        _dl_job["cancel_requested"] = True
    return {"ok": True}


def delete_model(name: str) -> dict:
    if name not in MODEL_NAMES:
        return {"ok": False, "error": "未知模型"}
    with _lock:
        if _dl_job["running"] and _dl_job.get("model") == name:
            return {"ok": False, "error": "该模型正在下载，请先取消"}
    try:
        _model_path(name).unlink(missing_ok=True)
        _model_path(name).with_suffix(".pt.part").unlink(missing_ok=True)
    except Exception as e:
        return {"ok": False, "error": str(e)[:200]}
    cfg = _load_cfg()
    if cfg.get("model") == name:
        cfg["model"] = ""
        _save_cfg(cfg)
    return {"ok": True, "model": name}


# ---------------------------------------------------------------- 转写生成字幕
def _seg_get(seg, key: str, default=0):
    """whisper 的 verbose=None 生成器产出的是 dict（不是对象），统一取值。"""
    if isinstance(seg, dict):
        return seg.get(key, default)
    return getattr(seg, key, default)


def _fmt_ts(sec: float) -> str:
    sec = max(0.0, float(sec))
    h = int(sec // 3600)
    m = int((sec % 3600) // 60)
    s = sec % 60
    return "%02d:%02d:%06.3f" % (h, m, s)


def _probe_duration(path: Path) -> float:
    ff = ffmpeg_tools.find_ffmpeg(required=False)
    try:
        r = subprocess.run([ff, "-hide_banner", "-i", str(path)], capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=60)
        m = re.search(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", r.stderr or "")
        if m:
            return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
    except Exception:
        pass
    return 0.0


def _gen_worker(media: Path) -> None:
    _cancel_gen.clear()
    wav = DATA_DIR / "temp_whisper.wav"
    try:
        if not deps_ready(force=True):
            raise RuntimeError("缺少语音识别依赖（openai-whisper），请先安装依赖")
        name = selected_model()
        if not name:
            raise RuntimeError("还没有选择 AI 字幕模型")
        mp = _model_path(name)
        if not (mp.exists() and mp.stat().st_size > 1024 * 1024):
            raise RuntimeError("所选模型 %s 还没下载" % name)
        with _lock:
            _gen_job.update({"running": True, "done": False, "ok": False, "media": str(media),
                             "srt": "", "percent": 0.0, "segments": 0, "language": "",
                             "error": "", "cancel_requested": False, "started_at": time.time()})
        ff = ffmpeg_tools.find_ffmpeg(required=True)
        try:
            wav.unlink(missing_ok=True)
        except Exception:
            pass
        rc = subprocess.run([ff, "-y", "-hide_banner", "-loglevel", "error", "-i", str(media),
                             "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(wav)],
                            capture_output=True, timeout=1800)
        if rc.returncode != 0 or not wav.exists():
            raise RuntimeError("音频提取失败（ffmpeg rc=%s）" % rc.returncode)
        dur = _probe_duration(media)
        import torch
        import whisper
        force = (os.environ.get("LMP_WHISPER_DEVICE") or "").strip().lower()
        if force == "cpu":
            dev = "cpu"
        elif force == "cuda":
            dev = "cuda"
        else:
            dev = "cuda" if torch.cuda.is_available() else "cpu"
        if dev == "cuda" and not torch.cuda.is_available():
            dev = "cpu"
        logger.info("AI 字幕转写：model=%s device=%s media=%s", name, dev, media)
        if dev == "cpu":
            logger.info("AI 字幕：没有可用 CUDA 显卡，使用 CPU 识别（速度会慢很多）")
        model = whisper.load_model(name, download_root=str(MODEL_DIR), device=dev)
        # ★ 每个模型单独存一版：<视频名>.ai.<模型>.srt
        srt_path = media.parent / (media.stem + ".ai." + name + ".srt")
        _ensure_zhconv()
        segs = []
        n = 0

        # ★ 不要把 wav 路径交给 whisper.transcribe —— 它内部会再调系统 `ffmpeg`，
        #   而安装版后端的 PATH 里没有 ffmpeg（会 WinError 2）。这里自己读 wav 成 float32 数组。
        import numpy as _np
        with wave.open(str(wav), "rb") as wf:
            _frames = wf.readframes(wf.getnframes())
        audio = _np.frombuffer(_frames, dtype=_np.int16).astype(_np.float32) / 32768.0
        total_samples = max(1, len(audio))
        sr = 16000
        chunk_samples = sr * 60          # 每段 60 秒：段间报进度 + 可取消（长视频不会一直卡在 0%）

        def write_srt() -> None:
            try:
                # ★ 必须 newline="" + 只用 \n：文本模式写 \r\n 会被 Python 再翻译一次，
                #   变成 CR CR LF，前端解析时会在时间行后多出一个空行 → 正文被当成空 → 字幕永远不显示。
                with open(srt_path, "w", encoding="utf-8", newline="") as f:
                    for i, s in enumerate(segs, 1):
                        f.write("%d\n%s --> %s\n%s\n\n" % (
                            i, _fmt_ts(s["start"]), _fmt_ts(s["end"]), s["text"]))
            except Exception:
                pass

        lang = ""
        for off in range(0, total_samples, chunk_samples):
            if _cancel_gen.is_set():
                raise RuntimeError("已取消")
            part = audio[off:off + chunk_samples]
            if len(part) == 0:
                break
            # verbose=False → 返回 dict（新版 whisper 的 transcribe 不再返回生成器）
            res = model.transcribe(part, language=None, task="transcribe",
                                   fp16=(dev == "cuda"), verbose=False,
                                   condition_on_previous_text=False)
            if isinstance(res, dict):
                lang = (res.get("language") or lang) or ""
                seg_list = res.get("segments") or []
            else:
                seg_list = res
            base_sec = off / float(sr)
            for s in seg_list:
                st = float(_seg_get(s, "start", 0) or 0) + base_sec
                en = float(_seg_get(s, "end", 0) or 0) + base_sec
                tx = (_seg_get(s, "text", "") or "").strip()
                if tx:
                    # ★ 中文统一转简体（large-v3 默认常输出繁体）
                    tx = _to_simplified(tx, lang)
                    segs.append({"start": st, "end": en, "text": tx})
            n = len(segs)
            with _lock:
                _gen_job["segments"] = n
                _gen_job["language"] = lang
                _gen_job["percent"] = round(min(99.0, (off + len(part)) / float(total_samples) * 100.0), 1)
            write_srt()
        write_srt()
        with _lock:
            _gen_job.update({"ok": True, "done": True, "running": False, "percent": 100.0,
                             "srt": str(srt_path), "model": name, "device": dev})
        logger.info("AI 字幕生成完成: %s（%d 条）", srt_path, n)
    except Exception as e:
        with _lock:
            _gen_job.update({"ok": False, "done": True, "running": False, "error": str(e)[:300]})
        logger.warning("AI 字幕生成失败: %s", e)
    finally:
        try:
            wav.unlink(missing_ok=True)
        except Exception:
            pass


def start_generate(media_path: str) -> dict:
    media = Path(media_path or "")
    if not media.is_file():
        return {"ok": False, "error": "媒体文件不存在"}
    if not deps_ready(force=True):
        return {"ok": False, "error": "还没有安装语音识别依赖（openai-whisper）", "need_deps": True}
    name = selected_model()
    if not name:
        return {"ok": False, "error": "请先选择 AI 字幕模型"}
    if not (_model_path(name).exists() and _model_path(name).stat().st_size > 1024 * 1024):
        return {"ok": False, "error": "所选模型还没下载：%s" % MODEL_LABELS.get(name, name), "need_model": True}
    with _lock:
        if _gen_job["running"]:
            return {"ok": False, "error": "已有转写任务进行中"}
        _gen_job.update({"running": True, "done": False, "ok": False, "media": str(media),
                         "srt": "", "percent": 0.0, "segments": 0, "language": "",
                         "model": name, "device": "",
                         "error": "", "cancel_requested": False, "started_at": time.time()})
    threading.Thread(target=_gen_worker, args=(media,), daemon=True, name="whisper-gen").start()
    return {"ok": True, "started": True, "model": name}


def cancel_generate() -> dict:
    _cancel_gen.set()
    with _lock:
        _gen_job["cancel_requested"] = True
    return {"ok": True}
