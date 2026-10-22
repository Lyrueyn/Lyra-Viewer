"""AI 增强依赖自安装（不打包进安装包，按本机显卡自动选版本在线安装）。

为什么单独一个模块：
  - PyTorch + torchvision/audio 单个就 2~3 GB，安装包不可能带；音频/视频增强器（AI 音频超分 LavaSR、
    人声降噪 DeepFilterNet、AI 视频超分 Real-ESRGAN、RIFE 补帧）都依赖它。
  - 安装版自带的是 embeddable Python 3.10，**连 pip 都没有** → 必须先 bootstrap 一个 pip。
  - 用户机器显卡/驱动不同 → 装的 CUDA wheel 也不同（RTX 50 系要 cu128/cu130，老卡 cu118/cu121）。

对外三个函数：
  env_info()        —— 本机环境（python / 显卡 / 驱动 / 将选用哪个 CUDA 版本 / 预计下载体积）
  status()          —— 依赖是否装好、每个功能是否可用、当前安装任务进度
  start_install()   —— 启动一键安装（幂等：已装好 / 正在装 都会直接返回，不重复下载）
"""
from __future__ import annotations

import json
import logging
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path

from .db import DATA_DIR

logger = logging.getLogger(__name__)

PYTHON = sys.executable          # 当前后端解释器（安装版 = <安装目录>\backend\python\python.exe）
MARKER = DATA_DIR / "ai_deps.json"
LOCK = DATA_DIR / "ai_deps.lock"
LOG_FILE = DATA_DIR / "ai_deps_install.log"

# 每个功能依赖哪些模块（用于安装后校验）
FEATURES = {
    "audio_sr": {
        "label": "AI 音频超分",
        "imports": ["torch", "soundfile", "huggingface_hub", "LavaSR.model"],
        "hint": "LavaSR（首次运行会从 HuggingFace 下 ~50MB 模型）",
    },
    "denoise": {
        "label": "人声降噪",
        "imports": ["torch", "torchaudio", "librosa", "soundfile", "df.enhance"],
        "hint": "DeepFilterNet（首次运行会下模型）",
    },
    "realesrgan": {
        "label": "AI 视频超分",
        "imports": ["torch", "torchvision", "basicsr.archs.rrdbnet_arch", "realesrgan"],
        "hint": "Real-ESRGAN（模型已随安装包提供）",
    },
    "rife": {
        "label": "RIFE 补帧",
        "imports": ["torch"],
        "hint": "RIFE（模型已随安装包提供）",
    },
}

# 各阶段权重（进度条用）
_STAGE_WEIGHTS = [
    ("bootstrap", "准备 pip 安装器", 3),
    ("torch", "下载安装 PyTorch（最大的一步，约 2~3 GB）", 55),
    ("audio", "安装音频处理包（librosa / soundfile 等）", 12),
    ("denoise", "安装人声降噪（DeepFilterNet）", 8),
    ("video", "安装 AI 视频超分（Real-ESRGAN / basicsr）", 10),
    ("lava", "安装 AI 音频超分（LavaSR）", 8),
    ("verify", "校验安装结果", 4),
]

_job_lock = threading.Lock()
_job = {
    "running": False,
    "done": False,
    "ok": False,
    "stage": "",
    "percent": 0.0,
    "logs": [],
    "error": "",
    "started_at": 0.0,
    "finished_at": 0.0,
}
_probe_cache: dict = {"ts": 0.0, "data": None}
_PIP_BOOTSTRAPPED = False
# ★ 下载/取消/进度相关全局（放在这里，worker / 监控线程 / 接口共用）
PIP_CACHE = DATA_DIR / "pip-cache"    # pip 下载缓存（重试复用）
WHEELS_DIR = DATA_DIR / "wheels"      # 自己下的 torch 轮子（真实进度 + 断点复用）
_current_proc = None                  # 当前正在跑的 pip 子进程（取消时 terminate）
_cancel_flag = threading.Event()      # 用户点了「取消安装」
_job_monitor: dict = {"stop": True}   # 下载监控线程停止标志


# ---------------------------------------------------------------- 工具函数
def _log(msg: str) -> None:
    line = time.strftime("%H:%M:%S ") + str(msg)
    logger.info("[ai_deps] %s", msg)
    with _job_lock:
        _job["logs"].append(line)
        if len(_job["logs"]) > 400:
            del _job["logs"][:-200]
    try:
        LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
        with LOG_FILE.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def _run(cmd: list, timeout: int = 7200, on_line=None, env: dict = None) -> tuple:
    """跑子进程并逐行回调（pip 输出）。返回 (returncode, tail_lines)。"""
    e = dict(os.environ)
    e["PYTHONIOENCODING"] = "utf-8"
    e["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"
    if env:
        e.update(env)
    tail: list = []
    global _current_proc
    try:
        proc = subprocess.Popen(
            cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", env=e,
            cwd=str(DATA_DIR),
        )
        _current_proc = proc
    except Exception as ex:
        _log("启动命令失败: {} ({})".format(" ".join(map(str, cmd))[:160], ex))
        return -1, [str(ex)]
    try:
        for line in proc.stdout:  # type: ignore[union-attr]
            line = (line or "").rstrip()
            if not line:
                continue
            tail.append(line)
            if len(tail) > 200:
                del tail[:100]
            if on_line:
                try:
                    on_line(line)
                except Exception:
                    pass
        rc = proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        try:
            proc.kill()
        except Exception:
            pass
        _log("命令超时（{} 分钟）".format(timeout // 60))
        return -2, tail
    finally:
        _current_proc = None
    return rc, tail


def _pip_install(args: list, on_line=None) -> int:
    _check_cancel()
    global _PIP_BOOTSTRAPPED
    if not _PIP_BOOTSTRAPPED:
        rc, _ = _run([PYTHON, "-m", "pip", "--version"], timeout=120)
        if rc != 0:
            return -3
        _PIP_BOOTSTRAPPED = True
    _log("pip install " + " ".join(args))
    # ★ 用持久化缓存目录（不用 --no-cache-dir）：
    #   ① 下载量可观测 → 主线程按缓存目录大小算"已下载 X MB / 共约 Y MB"（torch 2GB 阶段不会像死机）
    #   ② 中途取消/失败后重试可以复用已下载的 wheel，不用从头再来
    try:
        PIP_CACHE.mkdir(parents=True, exist_ok=True)
    except Exception:
        pass
    return _run([PYTHON, "-m", "pip", "install", "--cache-dir", str(PIP_CACHE),
                 "--no-warn-script-location"] + args,
                on_line=on_line)[0]


def _download(url: str, dst: Path, on_line=None, timeout: int = 600) -> bool:
    try:
        _log("下载 " + url)
        req = urllib.request.Request(url, headers={"User-Agent": "LocalMediaPlayer/1.0"})
        with urllib.request.urlopen(req, timeout=timeout) as r, open(dst, "wb") as f:
            total = int(r.headers.get("Content-Length") or 0)
            got = 0
            while True:
                chunk = r.read(1024 * 256)
                if not chunk:
                    break
                f.write(chunk)
                got += len(chunk)
                if on_line and total:
                    on_line("下载中 {:.1f}/{:.1f} MB".format(got / 1048576, total / 1048576))
        _log("下载完成 {}（{:.1f} MB）".format(dst.name, dst.stat().st_size / 1048576))
        return True
    except Exception as ex:
        _log("下载失败: {} ({})".format(url, str(ex)[:160]))
        return False


# ---------------------------------------------------------------- 环境检测
def _nvidia() -> tuple:
    """返回 (GPU 名称, 驱动版本)；没有 NVIDIA 显卡返回 ("", "")。"""
    for exe in ("nvidia-smi", r"C:\Windows\System32\nvidia-smi.exe",
                r"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"):
        try:
            r = subprocess.run([exe, "--query-gpu=name,driver_version", "--format=csv,noheader"],
                               capture_output=True, text=True, timeout=10)
            if r.returncode == 0 and r.stdout.strip():
                parts = [p.strip() for p in r.stdout.strip().splitlines()[0].split(",")]
                if len(parts) >= 2:
                    return parts[0], parts[1]
        except Exception:
            continue
    return "", ""


def _torch_mirrors(index_url: str) -> list:
    """官方源 + 国内镜像（阿里云 pytorch-wheels）。官方慢/被墙时自动换镜像重试。"""
    out = [index_url]
    m = re.search(r"/whl/(cu\d+|cpu)/?$", index_url)
    if m:
        tag = m.group(1)
        out.append("https://mirrors.aliyun.com/pytorch-wheels/{}".format(tag))
    return out

def _pick_torch_index(driver: str, gpu: str) -> tuple:
    """按驱动版本/显卡架构选 CUDA wheel 源，返回 (index_url, 说明)。"""
    if not gpu:
        return ("https://download.pytorch.org/whl/cpu",
                "没有检测到 NVIDIA 显卡 → 安装 CPU 版 PyTorch（AI 增强会慢很多，但能用）")
    try:
        major = int(str(driver).split(".")[0])
    except Exception:
        major = 0
    # PyTorch 各 CUDA 轮子要求的最低驱动（大致对应关系；取能用的最新）
    if major >= 580:
        return ("https://download.pytorch.org/whl/cu130", "驱动 {} + {} → CUDA 13.0 版本（cu130）".format(driver, gpu))
    if major >= 570:
        return ("https://download.pytorch.org/whl/cu128", "驱动 {} + {} → CUDA 12.8 版本（cu128）".format(driver, gpu))
    if major >= 560:
        return ("https://download.pytorch.org/whl/cu126", "驱动 {} + {} → CUDA 12.6 版本（cu126）".format(driver, gpu))
    if major >= 550:
        return ("https://download.pytorch.org/whl/cu124", "驱动 {} + {} → CUDA 12.4 版本（cu124）".format(driver, gpu))
    if major >= 525:
        return ("https://download.pytorch.org/whl/cu121", "驱动 {} + {} → CUDA 12.1 版本（cu121）".format(driver, gpu))
    return ("https://download.pytorch.org/whl/cu118", "驱动 {} + {} → CUDA 11.8 版本（cu118）".format(driver, gpu))


def env_info() -> dict:
    gpu, driver = _nvidia()
    index, note = _pick_torch_index(driver, gpu)
    return {
        "python": sys.version.split()[0],
        "python_exe": PYTHON,
        "gpu": gpu,
        "driver": driver,
        "torch_index": index,
        "torch_note": note,
        "estimate_mb": 3200 if gpu else 900,
        "marker_exists": MARKER.exists(),
    }


# ---------------------------------------------------------------- 依赖校验
def _probe_feature_imports(imports: list) -> tuple:
    """子进程里真实 import 一遍（不在后端常驻进程里占内存/装坏也不影响主进程）。"""
    backend_dir = str(Path(__file__).resolve().parent.parent)
    code = (
        "import sys, importlib, json, types\n"
        "sys.path.insert(0, %r)\n"
        "res = {}\n"
        # ★ 真实运行路径里的两个兼容补丁（增强器在用到时才打，这里提前打，校验才准）：
        #   1) basicsr 1.4.2 引用 torchvision.transforms.functional_tensor（新版 torchvision 已删）
        "try:\n"
        "    import torchvision.transforms.functional as _F\n"
        "    if 'torchvision.transforms.functional_tensor' not in sys.modules:\n"
        "        _ft = types.ModuleType('torchvision.transforms.functional_tensor')\n"
        "        _ft.rgb_to_grayscale = _F.rgb_to_grayscale\n"
        "        sys.modules['torchvision.transforms.functional_tensor'] = _ft\n"
        "except Exception:\n"
        "    pass\n"
        #   2) DeepFilterNet 0.5.6 引用已移除的 torchaudio.backend（用增强器自带的 shim）
        "try:\n"
        "    from app.enhancers.denoise import _ensure_torchaudio_shim\n"
        "    _ensure_torchaudio_shim()\n"
        "except Exception:\n"
        "    pass\n"
        "for m in %r:\n"
        "    try:\n"
        "        importlib.import_module(m)\n"
        "        res[m] = True\n"
        "    except Exception:\n"
        "        res[m] = False\n"
        "print(json.dumps(res))\n"
    ) % (backend_dir, imports)
    try:
        r = subprocess.run([PYTHON, "-c", code], capture_output=True, text=True,
                           timeout=120, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            return {}
        for line in reversed((r.stdout or "").strip().splitlines()):
            try:
                return json.loads(line)
            except Exception:
                continue
    except Exception:
        pass
    return {}


def _probe(force: bool = False) -> dict:
    """检测所有 AI 依赖（结果缓存 60 秒；安装中直接返回 job 状态，避免拖慢轮询）。"""
    now = time.time()
    with _job_lock:
        running = _job["running"]
    if not force and now - _probe_cache["ts"] < 60 and _probe_cache["data"] is not None:
        return _probe_cache["data"]

    all_imports = sorted({m for f in FEATURES.values() for m in f["imports"]})
    res = {m: False for m in all_imports}
    torch_ver = ""
    cuda = False
    if not running:
        got = _probe_feature_imports(all_imports)
        for m, ok in (got or {}).items():
            res[m] = bool(ok)
        if res.get("torch"):
            try:
                r = subprocess.run(
                    [PYTHON, "-c",
                     "import torch;print(torch.__version__);print('1' if torch.cuda.is_available() else '0');"
                     "print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')"],
                    capture_output=True, text=True, timeout=120, encoding="utf-8", errors="replace")
                lines = [x.strip() for x in (r.stdout or "").splitlines() if x.strip()]
                if lines:
                    torch_ver = lines[0]
                if len(lines) > 1:
                    cuda = lines[1] == "1"
            except Exception:
                pass

    features = {}
    for key, meta in FEATURES.items():
        ok = all(res.get(m, False) for m in meta["imports"])
        features[key] = {
            "label": meta["label"],
            "ready": ok,
            "missing": [m for m in meta["imports"] if not res.get(m, False)],
            "hint": meta["hint"],
        }
    data = {
        "ready": all(f["ready"] for f in features.values()),
        "torch": {"installed": bool(res.get("torch")), "version": torch_ver, "cuda": cuda},
        "features": features,
        "env": env_info(),
        "probed_at": now,
    }
    _probe_cache["ts"] = now
    _probe_cache["data"] = data
    return data


def feature_ready(key: str) -> tuple:
    """给 services._enhancer_deps 用：这个 AI 增强器现在能不能真跑。"""
    try:
        d = _probe()
    except Exception:
        return False, "依赖检测失败"
    f = (d.get("features") or {}).get(key)
    if not f:
        return True, ""
    if f.get("ready"):
        return True, ""
    if d.get("torch", {}).get("installed"):
        return False, "还缺组件：{}（打开播放器 → 增强任务 → 下拉到底点「一键下载 AI 依赖」补装）".format(
            "、".join(f.get("missing") or []))
    return False, ("没装 AI 依赖（PyTorch 约 2~3 GB，不随安装包提供）。"
                   "打开播放器 → 增强任务 → 一键下载 AI 依赖（自动匹配本机显卡）")


# ---------------------------------------------------------------- 安装
def _pid_alive(pid: int) -> bool:
    """Windows 下判断 pid 是否还活着（用来识别"上次安装进程被杀 → 锁是残的"）。"""
    try:
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        ctypes.windll.kernel32.CloseHandle(h)
        return True
    except Exception:
        return True   # 判断不了就当活着（宁可多等，也别并发下载）


def _lock_acquire() -> bool:
    """文件锁：防止两个进程同时装（幂等按钮防重复下载的第二道保险）。
    残锁处理：锁里记 pid，进程已不存在 → 直接清掉（否则杀过安装后按钮永远说"正在安装中"）。"""
    try:
        LOCK.parent.mkdir(parents=True, exist_ok=True)
        if LOCK.exists():
            stale = False
            try:
                raw = LOCK.read_text(encoding="utf-8").strip()
                pid = int(raw.split()[0]) if raw else 0
                age = time.time() - LOCK.stat().st_mtime
                if pid <= 0:
                    stale = True
                elif pid == os.getpid():
                    stale = True          # 自己的锁（上次异常残留）
                elif not _pid_alive(pid):
                    stale = True
                elif age > 6 * 3600:
                    stale = True
            except Exception:
                stale = True
            if stale:
                _log("清理残留安装锁（上次安装进程已退出）")
                try:
                    LOCK.unlink()
                except Exception:
                    pass
            else:
                return False
        LOCK.write_text(str(os.getpid()), encoding="utf-8")
        return True
    except Exception:
        return True


def _lock_release() -> None:
    try:
        LOCK.unlink()
    except Exception:
        pass


class _InstallCancelled(Exception):
    """用户点了「取消安装」。"""


def _check_cancel() -> None:
    if _cancel_flag.is_set():
        raise _InstallCancelled("用户取消")


def _set_stage(stage: str, percent: float) -> None:
    _check_cancel()
    with _job_lock:
        _job["stage"] = stage
        _job["percent"] = max(0.0, min(100.0, percent))

def _pip_progress_line(line: str) -> None:
    """把 pip 的下载/安装行变成用户能看懂的一句话。"""
    s = line.strip()
    if not s:
        return
    if s.startswith(("Downloading ", "Collecting ", "Installing collected", "Building wheel",
                     "Successfully installed", "Requirement already satisfied: torch")):
        if len(s) > 160:
            s = s[:160] + "…"
        _log(s)


# ================= PyTorch 轮子：自己下载（真实进度 + 可取消 + 断点复用）=================
def _update_download_bytes(done: int, total: int) -> None:
    with _job_lock:
        _job["bytes_done"] = int(done)
        _job["bytes_est"] = int(total)
        if total > 0:
            pct = 5.0 + 52.0 * min(1.0, float(done) / float(total))   # torch 阶段占总进度 5%~57%
            if pct > _job["percent"]:
                _job["percent"] = pct


def _simple_index_wheels(index_url: str, pkg: str, py_tag: str = "cp310") -> list:
    """从 pytorch 官方 simple index 里挑该包 py3.10/win_amd64 的 wheel URL。"""
    base = index_url.rstrip("/") + "/" + pkg + "/"
    try:
        req = urllib.request.Request(base, headers={"User-Agent": "LocalMediaPlayer/1.0"})
        with urllib.request.urlopen(req, timeout=40) as r:
            html = r.read().decode("utf-8", "replace")
    except Exception as e:
        _log("读取 {} 索引失败：{}".format(pkg, str(e)[:120]))
        return []
    hrefs = re.findall(r'href="([^"]+?\.whl)(?:#[^"]*)?"', html)
    out = []
    for h in hrefs:
        u = urllib.parse.urljoin(base, h.replace("&amp;", "&"))
        if "win_amd64" not in u or py_tag not in u:
            continue
        out.append(u)
    return out


def _wheel_version_key(url: str) -> tuple:
    name = url.rsplit("/", 1)[-1]
    m = re.search(r"-(\d+)\.(\d+)\.(\d+)", name)
    return (int(m.group(1)), int(m.group(2)), int(m.group(3))) if m else (0, 0, 0)


def _head_size(url: str) -> int:
    try:
        req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "LocalMediaPlayer/1.0"})
        with urllib.request.urlopen(req, timeout=40) as r:
            return int(r.headers.get("Content-Length") or 0)
    except Exception:
        return 0


def _http_download(url: str, dst: Path, on_progress) -> None:
    """带进度/可取消的下载（先写 .part，完成才改名，避免半截文件被当成完整轮子复用）。"""
    dst.parent.mkdir(parents=True, exist_ok=True)
    part = dst.with_suffix(dst.suffix + ".part")
    req = urllib.request.Request(url, headers={"User-Agent": "LocalMediaPlayer/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r, open(part, "wb") as f:
            total = int(r.headers.get("Content-Length") or 0)
            got = 0
            while True:
                _check_cancel()
                chunk = r.read(1024 * 256)
                if not chunk:
                    break
                f.write(chunk)
                got += len(chunk)
                try:
                    on_progress(got, total)
                except Exception:
                    pass
    except _InstallCancelled:
        try:
            part.unlink()
        except Exception:
            pass
        raise
    except Exception as e:
        try:
            part.unlink()
        except Exception:
            pass
        raise RuntimeError("下载失败：{}（{}）".format(url.rsplit("/", 1)[-1], str(e)[:120])) from e
    part.replace(dst)


def _install_torch_from_wheels(index_url: str) -> bool:
    """下载 torch/torchvision/torchaudio 三个轮子（各平台版本由官方 index 决定）再本地安装。
    返回 True=成功；False=走不通（回退 pip 直装）。"""
    try:
        pkgs = ["torch", "torchvision", "torchaudio"]
        picked = []
        for pkg in pkgs:
            urls = _simple_index_wheels(index_url, pkg)
            if not urls:
                _log("拿不到 {} 的 wheel 索引，回退 pip 安装".format(pkg))
                return False
            url = sorted(urls, key=_wheel_version_key)[-1]
            picked.append((pkg, url))
        # 先 HEAD 拿总大小（进度条用）
        total = 0
        sizes = {}
        for pkg, url in picked:
            sz = _head_size(url)
            sizes[url] = sz
            total += sz
        _log("PyTorch 三个轮子合计约 {:.0f} MB，开始下载（可随时点「取消安装」）…".format(total / 1048576))
        WHEELS_DIR.mkdir(parents=True, exist_ok=True)
        done_total = 0
        files = []
        for pkg, url in picked:
            fn = urllib.parse.unquote(url.rsplit("/", 1)[-1])
            dst = WHEELS_DIR / fn
            expected = sizes.get(url, 0)
            files.append(str(dst))
            have = dst.stat().st_size if dst.exists() else 0
            if dst.exists() and expected > 0 and have == expected:
                _log("已下载过 {}（{:.0f} MB）→ 复用，不重复下载".format(fn, have / 1048576))
                done_total += have
                _update_download_bytes(done_total, total)
                continue
            if dst.exists():
                try:
                    dst.unlink()
                except Exception:
                    pass
            _log("下载 {:.0f} MB ← {}".format(expected / 1048576 if expected else 0, fn))
            base = done_total
            _http_download(url, dst, lambda got, _t, _b=base: _update_download_bytes(_b + got, total))
            done_total += dst.stat().st_size
            _update_download_bytes(done_total, total)
        _log("轮子下载完成，开始安装（torch 用本地轮子，不会再下 2GB；剩余依赖走 PyPI）…")
        with _job_lock:
            _job["bytes_done"] = 0
            _job["bytes_est"] = 0      # 下载阶段结束 → 界面改为只看安装阶段进度，不再显示乱七八糟的总量
            _job["stage"] = "torch（安装中）"
        rc = _pip_install(files, on_line=_pip_progress_line)
        if rc != 0:
            _log("本地轮子安装失败（rc={}），回退 pip 直装".format(rc))
            return False
        return True
    except _InstallCancelled:
        raise
    except Exception as e:
        _log("轮子方式安装失败，回退 pip：{}".format(str(e)[:160]))
        return False

def _install_worker() -> None:
    global _PIP_BOOTSTRAPPED
    started = time.time()
    with _job_lock:
        _job.update({"running": True, "done": False, "ok": False, "stage": "", "percent": 0.0,
                     "logs": [], "error": "", "started_at": started, "finished_at": 0.0,
                     "bytes_done": 0, "bytes_est": 0})
    env = env_info()
    try:
        _log("开始安装 AI 依赖：{}".format(env["torch_note"]))
        _log("安装目录：{}".format(Path(PYTHON).parent.parent))

        # ① bootstrap pip（embeddable python 没有 pip）
        _set_stage("bootstrap", 1)
        rc, _ = _run([PYTHON, "-m", "pip", "--version"], timeout=120)
        if rc != 0:
            _log("当前 Python 没有 pip，先下载 get-pip.py 引导安装…")
            with tempfile.TemporaryDirectory() as td:
                gp = Path(td) / "get-pip.py"
                if not _download("https://bootstrap.pypa.io/get-pip.py", gp):
                    raise RuntimeError("下载 get-pip.py 失败（检查网络/代理）")
                rc2, tail = _run([PYTHON, str(gp), "--no-warn-script-location"], on_line=_pip_progress_line)
                if rc2 != 0:
                    raise RuntimeError("引导 pip 失败：{}".format(" | ".join(tail[-4:])))
            _PIP_BOOTSTRAPPED = False
        _set_stage("bootstrap", 3)

        # ② torch（不打包，体积最大；flit_core<4 是 py3.10 上的安装前置，实测必须）
        _set_stage("torch", 5)
        _pip_install(["\"flit_core<4\""], on_line=_pip_progress_line)
        # 已有 torch 但"GPU 机器上却是 CPU 版"→ 强制重装成 CUDA 版（否则功能能跑但慢得没法用）
        need_torch = True
        try:
            r = subprocess.run([PYTHON, "-c",
                                "import torch;print(torch.__version__);print('1' if torch.cuda.is_available() else '0')"],
                               capture_output=True, text=True, timeout=120, encoding="utf-8", errors="replace")
            lines = [x.strip() for x in (r.stdout or "").splitlines() if x.strip()]
            if r.returncode == 0 and len(lines) >= 2:
                has_cuda = lines[1] == "1"
                if not env["gpu"] or has_cuda:
                    need_torch = False
                    _log("已有 torch {}（cuda={}），跳过 torch 下载".format(lines[0], has_cuda))
                else:
                    _log("检测到 torch {} 但 CUDA 不可用（GPU 机器装了 CPU 版）→ 重装 CUDA 版".format(lines[0]))
        except Exception:
            pass
        if need_torch:
            # ★ 优先：直接按"轮子文件"下载（自己下 → 有真实字节进度 + 随时可取消 + 断点复用）
            #   走不通再退回 pip 直接装（pip 下载 2GB 时是黑盒，进度/取消都不好做）
            ok_torch = _install_torch_from_wheels(env["torch_index"])
            if not ok_torch:
                for idx in _torch_mirrors(env["torch_index"]):
                    _log("torch 源：" + idx)
                    rc = _pip_install(["torch", "torchvision", "torchaudio", "--index-url", idx],
                                      on_line=_pip_progress_line)
                    if rc == 0:
                        ok_torch = True
                        break
                    _log("该源安装失败（rc={}），换下一个源重试…".format(rc))
            if not ok_torch:
                raise RuntimeError("PyTorch 安装失败（网络太慢/磁盘不足？详见安装日志；装好后重试会跳过已装部分）")
        _set_stage("torch", 58)

        # ③ 音频基础包
        _set_stage("audio", 60)
        _pip_install(["numpy==1.26.4", "scipy==1.15.3", "soundfile==0.14.0", "librosa==0.11.0"],
                     on_line=_pip_progress_line)
        _set_stage("audio", 70)

        # ④ 人声降噪 DeepFilterNet
        _set_stage("denoise", 70)
        _pip_install(["deepfilternet==0.5.6"], on_line=_pip_progress_line)
        _set_stage("denoise", 78)

        # ⑤ AI 视频超分
        _set_stage("video", 78)
        # basicsr 的 setup 会 import torch → 必须 --no-build-isolation（否则隔离环境里没 torch 装不上）
        _run([PYTHON, "-m", "pip", "install", "--no-cache-dir", "--no-warn-script-location",
              "--no-build-isolation", "basicsr==1.4.2"], on_line=_pip_progress_line)
        _pip_install(["realesrgan==0.3.0", "opencv-python"], on_line=_pip_progress_line)
        _set_stage("video", 88)

        # ⑥ AI 音频超分 LavaSR（PyPI 没有就下 GitHub 源码包；不依赖 git）
        _set_stage("lava", 88)
        rc = _pip_install(["LavaSR"], on_line=_pip_progress_line)
        if rc != 0:
            _log("PyPI 上没有 LavaSR，改从 GitHub 源码安装…")
            with tempfile.TemporaryDirectory() as td:
                z = Path(td) / "LavaSR-main.zip"
                if not _download("https://github.com/ysharma3501/LavaSR/archive/refs/heads/main.zip", z):
                    raise RuntimeError("下载 LavaSR 源码失败")
                rc = _run([PYTHON, "-m", "pip", "install", "--no-cache-dir",
                           "--no-warn-script-location", "--no-deps", str(z)],
                          on_line=_pip_progress_line)[0]
                if rc != 0:
                    raise RuntimeError("LavaSR 安装失败")
        _pip_install(["huggingface_hub"], on_line=_pip_progress_line)
        _set_stage("lava", 96)

        # ⑦ 校验
        _set_stage("verify", 97)
        _probe_cache["ts"] = 0.0
        probe = _probe(force=True)
        bad = [k for k, f in probe["features"].items() if not f["ready"]]
        MARKER.write_text(json.dumps({
            "ok": not bad,
            "installed_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "torch": probe["torch"],
            "features": {k: f["ready"] for k, f in probe["features"].items()},
            "env": probe["env"],
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        with _job_lock:
            _job["percent"] = 100.0
            _job["stage"] = "完成"
            _job["ok"] = not bad
            _job["done"] = True
            if bad:
                _job["error"] = "以下功能校验未通过：{}".format("、".join(FEATURES[k]["label"] for k in bad))
            else:
                _job["logs"].append("全部 AI 增强依赖安装完成 ✔（torch {} cuda={}）".format(
                    probe["torch"].get("version", "?"), probe["torch"].get("cuda")))
        _log("安装结束：ok={} 用时 {:.0f} 秒".format(not bad, time.time() - started))
    except _InstallCancelled:
        _log("安装已取消（已下载缓存保留，下次点安装会接着用）")
        with _job_lock:
            _job["ok"] = False
            _job["done"] = True
            _job["error"] = "已取消"
            _job["stage"] = "已取消"
    except Exception as ex:
        logger.warning("AI 依赖安装失败: %s", ex)
        _log("安装失败：" + str(ex)[:400])
        with _job_lock:
            _job["ok"] = False
            _job["done"] = True
            _job["error"] = str(ex)[:400]
        try:
            MARKER.write_text(json.dumps({"ok": False, "error": str(ex)[:400],
                                          "at": time.strftime("%Y-%m-%d %H:%M:%S")},
                                         ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception:
            pass
    finally:
        _job_monitor["stop"] = True
        with _job_lock:
            _job["running"] = False
            _job["finished_at"] = time.time()
        _lock_release()
        _probe_cache["ts"] = 0.0


def _dir_size_mb(path: Path) -> float:
    total = 0
    try:
        for f in path.rglob("*"):
            try:
                if f.is_file():
                    total += f.stat().st_size
            except Exception:
                pass
    except Exception:
        pass
    return total / 1048576.0


def _monitor_download_job(est_mb: int) -> None:
    """后台监控 pip 缓存目录大小 → 换算成"已下载/总大小/百分比"，让 2GB 的 torch 阶段也有进度动。"""
    base = 3.0
    while not _job_monitor.get("stop"):
        try:
            with _job_lock:
                stage = _job["stage"]
                running = _job["running"]
            if not running:
                break
            got = _dir_size_mb(PIP_CACHE) if PIP_CACHE.exists() else 0.0
            frac = min(0.97, got / max(1.0, float(est_mb)))
            # torch 阶段占主要时间：把"下载量占比"映射到 5%→95%
            pct = max(base, min(95.0, 3.0 + 92.0 * frac))
            with _job_lock:
                _job["bytes_done"] = int(got * 1048576)
                _job["bytes_est"] = int(est_mb * 1048576)
                # 只在比"阶段进度"更靠前时用下载量百分比，避免进度倒退
                if pct > _job["percent"] and _job["stage"] not in ("", "verify", "完成"):
                    _job["percent"] = pct
        except Exception:
            pass
        time.sleep(1.0)


def cancel_install() -> dict:
    """取消正在进行的依赖安装（杀 pip 子进程；已下载的缓存保留，重新点会接着用）。"""
    with _job_lock:
        running = _job["running"]
    if not running:
        return {"action": "not_running", "message": "当前没有正在进行的安装"}
    _cancel_flag.set()
    _job_monitor["stop"] = True
    p = _current_proc
    if p is not None:
        try:
            p.terminate()
        except Exception:
            pass
        time.sleep(1)
        try:
            if p.poll() is None:
                p.kill()
        except Exception:
            pass
    _log("用户取消了 AI 依赖安装（已下载的缓存会保留）")
    return {"action": "cancelling", "message": "已取消安装（已下载的缓存保留，下次点安装会接着用）"}

def start_install() -> dict:
    """一键安装（幂等）。返回 {action: started|already_running|already_ready}"""
    with _job_lock:
        if _job["running"]:
            return {"action": "already_running", "message": "依赖正在下载安装中，请等进度条走完（不会重复下载）"}
    try:
        probe = _probe()
        if probe.get("ready"):
            return {"action": "already_ready", "message": "AI 依赖已经装好了，无需重复下载"}
    except Exception:
        pass
    if not _lock_acquire():
        return {"action": "already_running", "message": "另一个安装进程正在运行，请稍候"}
    _cancel_flag.clear()
    try:
        env = env_info()
        est = int(env.get("estimate_mb") or 3200)
    except Exception:
        est = 3200
    _job_monitor["stop"] = True   # ★ 不再启动 pip 缓存监控：真实下载量由轮子下载循环直接上报，避免两个数据源打架
    threading.Thread(target=_install_worker, daemon=True, name="ai-deps-install").start()
    return {"action": "started", "message": "开始下载安装 AI 依赖（首次约 2~3 GB，请保持网络畅通；可随时取消）"}


def status() -> dict:
    probe = _probe()
    with _job_lock:
        job = {
            "running": _job["running"],
            "done": _job["done"],
            "ok": _job["ok"],
            "stage": _job["stage"],
            "percent": round(_job["percent"], 1),
            "error": _job["error"],
            "bytes_done": _job.get("bytes_done", 0),
            "bytes_est": _job.get("bytes_est", 0),
            "cancel_requested": _cancel_flag.is_set(),
            "logs": _job["logs"][-12:],
            "elapsed": int((_job["finished_at"] or time.time()) - _job["started_at"]) if _job["started_at"] else 0,
        }
    return {"ok": True, "deps": probe, "job": job, "marker": str(MARKER)}
