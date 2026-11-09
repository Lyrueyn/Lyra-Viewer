#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
analyze_audio_dump.py —— 播放器音频 dump（audio_dump.f32）量化分析工具

用途：倍速（atempo，保持音高）验收。引擎在设了环境变量 DSH_AUDIO_DUMP=1 时，
      把**真正交给声卡的最终数据**（含音量增益斜坡与淡入）写成 audio_dump.f32：
      float32 小端、交错、默认 48000Hz、2 声道。同一段媒体分别用 1.0× / 1.5× / 2.0×
      播一遍，比对下列指标即可判断"时长是否按倍速缩短、音高（基频）是否没变、
      有没有变调/爆音/长静音"。

输出指标：
  ① 样本总数（帧数、每声道样本数）、时长
  ② 峰值（dBFS）、RMS（dBFS，整体 + 每声道）
  ③ 相邻样本最大跳变（逐声道算，交错数据跨声道相邻无意义）+ 出现位置
  ④ 连续静音段（默认阈值 -80dBFS、长度 > 5ms）列表
  ⑤ 1024 / 4096 点 FFT 主峰频率（Hann 窗、多帧平均、抛物线插值），
     以及局部最强的若干谱峰、频段能量占比（变调会让谱峰整体搬移）

约定（重要，读结论前先看）：
  * 本文件里 frequency/Hz 的"采样率"指 **dump 的采样率**（= 设备输出率，默认 48000）。
    引擎做 atempo 变速时输出仍是 48kHz，所以"谱峰频率不变"= 没变调。
  * 时长（duration）在 2.0× 下应当 ≈ 1.0× 的一半；这是判定"倍速真的生效"的第一指标。

用法：
  :: 分析（默认先找当前目录，再找播放器 bin 目录里的 audio_dump.f32）
  backend\\.venv\\Scripts\\python.exe backend\\tools\\analyze_audio_dump.py
  backend\\.venv\\Scripts\\python.exe backend\\tools\\analyze_audio_dump.py D:\\path\\audio_dump.f32 --rate 48000 --channels 2
  :: 存 JSON（供 --compare 用）
  ... analyze_audio_dump.py --json run_2.0x.json --label 2.0x
  :: 三档对照表（1.0x / 1.5x / 2.0x）
  ... analyze_audio_dump.py --compare run_1.0x.json run_1.5x.json run_2.0x.json
  :: 顺带把引擎日志里的"欠载 N 次"读出来打印
  ... analyze_audio_dump.py --log <bin>\\dsh_engine.log

只用 numpy，不需要安装任何东西。
"""

import argparse
import json
import math
import os
import re
import sys

import numpy as np

# ---------------------------------------------------------------- 基本参数
DEF_RATE = 48000          # 引擎 dump 的是"设备输出率"，本机为 48000
DEF_CHANNELS = 2
DEF_SILENCE_DB = -80.0    # 静音判定阈值（dBFS）
DEF_SILENCE_MIN_MS = 5.0  # 只报长度 > 5ms 的静音段
DEF_MAX_LIST = 20         # 静音段列表最多打印几条
FFT_SIZES = (1024, 4096)
BANDS = [(0, 100), (100, 300), (300, 1000), (1000, 3000), (3000, 6000), (6000, 12000), (12000, 24000)]

BIN_REL = os.path.join("WinUIGui", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "audio_dump.f32")


def db(x: float, floor: float = -200.0) -> float:
    """线性幅度 → dBFS（0 输入给 floor，避免 -inf）。"""
    if x <= 0:
        return floor
    return 20.0 * math.log10(x)


def find_default_dump() -> str:
    """默认 dump 路径：先当前目录，再仓库里的播放器 bin 目录。"""
    if os.path.isfile("audio_dump.f32"):
        return os.path.abspath("audio_dump.f32")
    here = os.path.abspath(os.path.dirname(__file__))
    # backend/tools → 仓库根
    root = os.path.abspath(os.path.join(here, "..", ".."))
    cand = os.path.join(root, BIN_REL)
    if os.path.isfile(cand):
        return cand
    return os.path.abspath("audio_dump.f32")


# ---------------------------------------------------------------- 读取
def load_dump(path: str, rate: int, channels: int):
    raw = np.fromfile(path, dtype="<f4")
    total_scalars = int(raw.size)
    if channels <= 0:
        raise ValueError("channels 必须 >= 1")
    usable = total_scalars - (total_scalars % channels)
    tail_extra = total_scalars - usable
    raw = raw[:usable]
    frames = usable // channels
    x = raw.reshape(frames, channels) if frames else np.zeros((0, channels), dtype=np.float32)
    return x, total_scalars, tail_extra, rate


# ---------------------------------------------------------------- 静音段
def silence_segments(x: np.ndarray, rate: int, silence_db: float, min_ms: float):
    """帧级静音（该帧所有声道都 <= 阈值）合并成段，返回 [(起s, 止s, 时长ms)]。"""
    if x.shape[0] == 0:
        return [], 0
    thr = 10.0 ** (silence_db / 20.0)
    frame_max = np.max(np.abs(x), axis=1)
    silent = frame_max <= thr
    n = silent.size
    segs = []
    i = 0
    min_frames = max(1, int(round(rate * min_ms / 1000.0)))
    while i < n:
        if silent[i]:
            j = i
            while j + 1 < n and silent[j + 1]:
                j += 1
            length = j - i + 1
            if length > min_frames:
                segs.append((i / rate, (j + 1) / rate, length * 1000.0 / rate))
            i = j + 1
        else:
            i += 1
    silent_frames = int(np.count_nonzero(silent))
    return segs, silent_frames


# ---------------------------------------------------------------- 最大跳变
def max_jump(x: np.ndarray, rate: int):
    """逐声道 |x[i+1]-x[i]| 的最大值 + 位置（秒、声道号）。"""
    if x.shape[0] < 2:
        return 0.0, 0.0, -1
    d = np.abs(np.diff(x, axis=0))
    per_ch = np.max(d, axis=0)
    ch = int(np.argmax(per_ch))
    val = float(per_ch[ch])
    idx = int(np.argmax(d[:, ch]))
    return val, (idx + 1) / rate, ch


# ---------------------------------------------------------------- 频谱
def _spectrum(x_mono: np.ndarray, rate: int, nfft: int, max_frames: int = 200):
    """多帧 Hann 窗平均幅度谱（帧间 hop=nfft/2，最多 max_frames 帧，均匀抽样）。"""
    if x_mono.size < nfft:
        return None, None
    hop = nfft // 2
    starts = np.arange(0, x_mono.size - nfft + 1, hop)
    if starts.size == 0:
        return None, None
    if starts.size > max_frames:
        pick = np.linspace(0, starts.size - 1, max_frames).astype(np.int64)
        starts = starts[pick]
    win = np.hanning(nfft).astype(np.float64)
    acc = np.zeros(nfft // 2 + 1, dtype=np.float64)
    for s in starts:
        seg = x_mono[s:s + nfft].astype(np.float64)
        seg = seg - seg.mean()                       # 去直流，避免 0Hz 假峰
        acc += np.abs(np.fft.rfft(seg * win)) ** 2   # 功率谱
    acc /= starts.size
    freqs = np.fft.rfftfreq(nfft, d=1.0 / rate)
    return freqs, acc


def _top_peaks(freqs, power, count=5, min_hz=20.0, min_rel=0.02):
    """局部极大值里取最强的若干个（抛物线插值细化频率）。"""
    if freqs is None or power is None or power.size < 3:
        return []
    p = power.copy()
    p[freqs < min_hz] = 0.0
    if p.max() <= 0:
        return []
    peaks = []
    for i in range(1, p.size - 1):
        if p[i] >= p[i - 1] and p[i] > p[i + 1] and p[i] >= p.max() * min_rel:
            a, b, c = p[i - 1], p[i], p[i + 1]
            denom = (a - 2 * b + c)
            delta = 0.0 if abs(denom) < 1e-20 else 0.5 * (a - c) / denom
            delta = max(-0.5, min(0.5, delta))
            df = freqs[1] - freqs[0]
            peaks.append((float(freqs[i] + delta * df), float(p[i])))
    peaks.sort(key=lambda t: -t[1])
    return peaks[:count]


def spectrum_report(x: np.ndarray, rate: int):
    """1024/4096 点主峰 + 最强谱峰列表 + 频段能量占比（用单声道下混）。"""
    if x.shape[0] == 0:
        return {}
    mono = x.mean(axis=1) if x.shape[1] > 1 else x[:, 0]
    out = {}
    for nfft in FFT_SIZES:
        freqs, power = _spectrum(mono, rate, nfft)
        if freqs is None:
            out["fft%d" % nfft] = {"available": False, "reason": "样本不足 %d 帧" % nfft}
            continue
        peaks = _top_peaks(freqs, power, 5)
        total = float(power.sum())
        bands = []
        for lo, hi in BANDS:
            if lo >= rate / 2:
                continue
            m = (freqs >= lo) & (freqs < min(hi, rate / 2))
            e = float(power[m].sum())
            bands.append({"band": "%d-%dHz" % (lo, hi), "pct": (100.0 * e / total) if total > 0 else 0.0})
        out["fft%d" % nfft] = {
            "available": True,
            "dominant_hz": round(peaks[0][0], 2) if peaks else 0.0,
            "peaks_hz": [round(p[0], 2) for p in peaks],
            "bands": [{"band": b["band"], "pct": round(b["pct"], 2)} for b in bands],
        }
    return out


def loudest_window_hz(x: np.ndarray, rate: int, win_s: float = 1.0, nfft: int = 4096):
    """在"最响的 1 秒窗口"上再算一次 1024/4096 主峰：整曲平均谱会被弱段稀释，这个更贴"当前在放什么音"。"""
    if x.shape[0] < nfft:
        return {}
    mono = x.mean(axis=1) if x.shape[1] > 1 else x[:, 0]
    w = max(nfft, int(win_s * rate))
    if mono.size <= w:
        starts = np.array([0])
    else:
        hop = max(1, w // 4)
        starts = np.arange(0, mono.size - w + 1, hop)
    if starts.size == 0:
        return {}
    seg_rms = np.array([float(np.sqrt(np.mean(mono[s:s + w].astype(np.float64) ** 2))) for s in starts])
    s0 = int(starts[int(np.argmax(seg_rms))])
    seg = mono[s0:s0 + w]
    res = {"window_start_s": round(s0 / rate, 3), "window_rms_dbfs": round(db(float(np.sqrt(np.mean(seg.astype(np.float64) ** 2)))), 2)}
    for n in FFT_SIZES:
        freqs, power = _spectrum(seg, rate, n, max_frames=60)
        peaks = _top_peaks(freqs, power, 3) if freqs is not None else []
        res["fft%d_hz" % n] = round(peaks[0][0], 2) if peaks else 0.0
    return res


# ---------------------------------------------------------------- 引擎日志
def parse_engine_log(path: str):
    """抓 dsh_engine.log 里的 '欠载 N 次' / '欠载=N'、'音频进度: N 秒，位置 X s' 行。

    日志每行形如 "[<GetTickCount64>] 音频进度: 6 秒，位置 6.0 s，RMS ...，欠载 6 次，共享"，
    有了行首毫秒时间戳，就能算出**实测倍速** = 媒体位置推进量 ÷ 墙钟时间。
    这是"倍速是否真的生效"最直接的数字（dump 的样本数只反映墙钟时长，反映不了推进速度）。
    """
    if not path or not os.path.isfile(path):
        return {}
    under = []
    progress = []
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            mt = re.match(r"\[(\d+)\]\s*(.*)$", line.strip())
            tick = int(mt.group(1)) if mt else None
            msg = mt.group(2) if mt else line
            m = re.search(r"欠载\s*=?\s*(\d+)\s*次?", msg)   # 两种格式都吃：'欠载=12' 与 '欠载 12 次'
            if m:
                under.append(int(m.group(1)))
            m2 = re.search(r"音频进度:\s*(\d+)\s*秒，位置\s*([\d.]+)\s*s.*?欠载\s*(\d+)\s*次", msg)
            if m2:
                progress.append({"sec": int(m2.group(1)), "pos_s": float(m2.group(2)),
                                 "underruns": int(m2.group(3)), "tick_ms": tick})

    # 实测倍速：取最长的一段"位置单调递增"的进度记录，用 位置差 ÷ 时间差
    rate = None
    rate_span = None
    with_tick = [p for p in progress if p["tick_ms"] is not None]
    best = []
    cur = []
    for p in with_tick:
        if cur and (p["pos_s"] <= cur[-1]["pos_s"] or p["tick_ms"] <= cur[-1]["tick_ms"]):
            if len(cur) > len(best):
                best = cur
            cur = []
        cur.append(p)
    if len(cur) > len(best):
        best = cur
    if len(best) >= 3:
        dt = (best[-1]["tick_ms"] - best[0]["tick_ms"]) / 1000.0
        dp = best[-1]["pos_s"] - best[0]["pos_s"]
        if dt > 2.0 and dp > 0:
            rate = dp / dt
            rate_span = {"from_s": best[0]["pos_s"], "to_s": best[-1]["pos_s"],
                         "wall_s": round(dt, 3), "media_s": round(dp, 3)}

    return {
        "log": path,
        "underrun_samples": len(under),
        "underrun_last": under[-1] if under else 0,
        "underrun_max": max(under) if under else 0,
        "media_rate": round(rate, 4) if rate else None,     # 实测倍速（媒体秒/墙钟秒）
        "media_rate_span": rate_span,
        "progress_lines": progress[-6:],
    }


# ---------------------------------------------------------------- 主分析
def analyze(path: str, rate: int, channels: int, silence_db: float, min_ms: float, max_list: int):
    if not os.path.isfile(path):
        raise SystemExit("找不到 dump 文件: %s" % path)
    x, total_scalars, tail_extra, rate = load_dump(path, rate, channels)
    frames = int(x.shape[0])
    dur = frames / float(rate) if rate else 0.0
    if frames == 0:
        raise SystemExit("dump 是空的（0 个可解析帧）: %s" % path)

    peak = float(np.max(np.abs(x)))
    per_ch_peak = [float(v) for v in np.max(np.abs(x), axis=0)]
    rms = float(np.sqrt(np.mean(x.astype(np.float64) ** 2)))
    per_ch_rms = [float(np.sqrt(np.mean(x[:, c].astype(np.float64) ** 2))) for c in range(x.shape[1])]
    jump, jump_t, jump_ch = max_jump(x, rate)
    segs, silent_frames = silence_segments(x, rate, silence_db, min_ms)

    rep = {
        "file": os.path.abspath(path),
        "bytes": os.path.getsize(path),
        "rate": rate,
        "channels": channels,
        "total_scalars": total_scalars,
        "frames": frames,                       # 每声道样本数（= 样本总数 / 声道数）
        "frames_per_channel": frames,
        "tail_extra_scalars": tail_extra,       # 非 0 说明 dump 尾巴被截断（进程被杀），正常应 0
        "duration_s": round(dur, 4),
        "peak": round(peak, 6),
        "peak_dbfs": round(db(peak), 2),
        "peak_per_channel_dbfs": [round(db(v), 2) for v in per_ch_peak],
        "rms": round(rms, 6),
        "rms_dbfs": round(db(rms), 2),
        "rms_per_channel_dbfs": [round(db(v), 2) for v in per_ch_rms],
        "max_jump": round(jump, 6),
        "max_jump_at_s": round(jump_t, 4),
        "max_jump_channel": jump_ch,
        "silence_threshold_dbfs": silence_db,
        "silence_min_ms": min_ms,
        "silence_count": len(segs),
        "silence_total_ms": round(sum(s[2] for s in segs), 2),
        "silence_total_pct": round(100.0 * silent_frames / frames, 3),
        "silence_segments": [{"start_s": round(a, 4), "end_s": round(b, 4), "ms": round(c, 2)} for a, b, c in segs[:max_list]],
        "silence_segments_truncated": max(0, len(segs) - max_list),
        "spectrum": spectrum_report(x, rate),
        "loudest_window": loudest_window_hz(x, rate),
    }
    return rep


# ---------------------------------------------------------------- 打印
def print_report(rep: dict, max_list: int):
    print("=" * 74)
    print("音频 dump 分析：%s" % rep["file"])
    print("=" * 74)
    print("格式      : float32 小端 · 交错 · %d 声道 · %d Hz" % (rep["channels"], rep["rate"]))
    print("文件大小  : %d 字节（%.2f MB）" % (rep["bytes"], rep["bytes"] / 1048576.0))
    print("样本总数  : %d 个 float（每声道 %d 帧）%s"
          % (rep["total_scalars"], rep["frames"],
             "" if rep["tail_extra_scalars"] == 0 else "  ⚠ 尾部多出 %d 个残缺样本（进程被杀/未 flush）" % rep["tail_extra_scalars"]))
    print("时长      : %.3f 秒（= 帧数 / %d）" % (rep["duration_s"], rep["rate"]))
    print("-" * 74)
    print("峰值      : %.6f（%.2f dBFS）  每声道 %s"
          % (rep["peak"], rep["peak_dbfs"], rep["peak_per_channel_dbfs"]))
    print("RMS       : %.6f（%.2f dBFS）  每声道 %s"
          % (rep["rms"], rep["rms_dbfs"], rep["rms_per_channel_dbfs"]))
    print("最大跳变  : %.6f（相邻样本，出现在 %.3f s，声道 %d）"
          % (rep["max_jump"], rep["max_jump_at_s"], rep["max_jump_channel"]))
    print("-" * 74)
    print("静音段    : 共 %d 段（阈值 %.1f dBFS，只算 > %.1f ms）；静音总时长 %.2f ms（占 %.3f%%）"
          % (rep["silence_count"], rep["silence_threshold_dbfs"], rep["silence_min_ms"],
             rep["silence_total_ms"], rep["silence_total_pct"]))
    if rep["silence_count"] == 0:
        print("            （无 —— 没有出现长静音，好事）")
    else:
        for s in rep["silence_segments"]:
            print("            %.3f s → %.3f s  (%.2f ms)" % (s["start_s"], s["end_s"], s["ms"]))
        if rep["silence_segments_truncated"]:
            print("            …还有 %d 段未列出" % rep["silence_segments_truncated"])
    print("-" * 74)
    for key in ("fft1024", "fft4096"):
        sp = rep["spectrum"].get(key, {})
        if not sp.get("available"):
            print("%-9s : 不可用（%s）" % (key, sp.get("reason", "?")))
            continue
        print("%-9s : 主峰 %.2f Hz  |  最强谱峰 %s Hz" % (key, sp["dominant_hz"], sp["peaks_hz"]))
        print("            频段能量: %s" % "  ".join("%s=%.1f%%" % (b["band"], b["pct"]) for b in sp["bands"]))
    lw = rep.get("loudest_window") or {}
    if lw:
        print("最响1秒窗 : 起点 %.3f s，RMS %.2f dBFS，FFT1024 主峰 %s Hz，FFT4096 主峰 %s Hz"
              % (lw.get("window_start_s", 0), lw.get("window_rms_dbfs", 0),
                 lw.get("fft1024_hz", 0), lw.get("fft4096_hz", 0)))
    print("=" * 74)


def print_log(logrep: dict):
    if not logrep:
        print("（未提供 --log，或日志里没有'欠载 N 次'）")
        return
    print("引擎日志  : %s" % logrep["log"])
    print("欠载次数  : 采样 %d 次，最后 %d 次，峰值 %d 次"
          % (logrep["underrun_samples"], logrep["underrun_last"], logrep["underrun_max"]))
    if logrep.get("media_rate"):
        sp = logrep["media_rate_span"]
        print("实测倍速  : %.3f×（媒体 %.1fs ÷ 墙钟 %.1fs；位置 %.1fs → %.1fs）"
              % (logrep["media_rate"], sp["media_s"], sp["wall_s"], sp["from_s"], sp["to_s"]))
    for p in logrep["progress_lines"]:
        print("            进度 %d 秒：媒体位置 %.1f s，欠载 %d 次" % (p["sec"], p["pos_s"], p["underruns"]))


# ---------------------------------------------------------------- 对照
def parse_label_speed(path: str, rep: dict):
    """从 --label 或文件名里抠出倍速（1.0x / 1.5 / 2x 之类）；抠不到返回 None。"""
    lab = rep.get("label") or os.path.basename(path)
    m = re.search(r"(\d+(?:\.\d+)?)\s*[xX×]", lab)
    if m:
        return float(m.group(1))
    return None


def compare(files, max_list: int):
    reps = []
    for f in files:
        if not os.path.isfile(f):
            print("⚠ 跳过不存在的 JSON: %s" % f)
            continue
        with open(f, "r", encoding="utf-8") as fh:
            reps.append(json.load(fh))
    if not reps:
        raise SystemExit("没有可对照的 JSON（先用 --json 生成）")

    rows = []
    for r in reps:
        speed = parse_label_speed(r.get("file", ""), r)
        sp4 = r.get("spectrum", {}).get("fft4096", {})
        lw = r.get("loudest_window") or {}
        rows.append({
            "label": r.get("label") or os.path.basename(r.get("file", "?")),
            "speed": speed,
            "frames": r.get("frames"),
            "duration_s": r.get("duration_s"),
            "peak_dbfs": r.get("peak_dbfs"),
            "rms_dbfs": r.get("rms_dbfs"),
            "max_jump": r.get("max_jump"),
            "silence_count": r.get("silence_count"),
            "silence_total_ms": r.get("silence_total_ms"),
            "hz4096": sp4.get("dominant_hz") if sp4.get("available") else None,
            "hz4096_loud": lw.get("fft4096_hz"),
            "underruns": (r.get("engine_log") or {}).get("underrun_last"),
            "media_rate": (r.get("engine_log") or {}).get("media_rate"),
        })

    print("=" * 108)
    print("倍速对照表（同一段媒体、不同倍速各 dump 一次）")
    print("=" * 108)
    hdr = "%-8s %-7s %10s %10s %9s %9s %9s %7s %9s %9s %9s %7s" % (
        "标签", "倍速", "帧数", "时长s", "峰值dB", "RMS dB", "最大跳变", "静音段", "静音ms", "基频Hz", "实测倍速", "欠载")
    print(hdr)
    print("-" * 108)
    for r in rows:
        print("%-8s %-7s %10s %10s %9s %9s %9s %7s %9s %9s %9s %7s" % (
            str(r["label"])[:8],
            ("%.2f×" % r["speed"]) if r["speed"] else "-",
            r["frames"], r["duration_s"], r["peak_dbfs"], r["rms_dbfs"],
            r["max_jump"], r["silence_count"], r["silence_total_ms"],
            r["hz4096"] if r["hz4096"] else "-",
            ("%.3f×" % r["media_rate"]) if r["media_rate"] else "-",
            r["underruns"] if r["underruns"] is not None else "-"))
    print("-" * 108)

    # 以"倍速最接近 1.0 的那次"当基准
    base = None
    for r in rows:
        if r["speed"] and abs(r["speed"] - 1.0) < 1e-6:
            base = r
            break
    if base is None:
        base = rows[0]
    print("基准 = %s（倍速 %s）：时长 %.3fs，基频 %s Hz，实测倍速 %s" % (
        base["label"], ("%.2f×" % base["speed"]) if base["speed"] else "?", base["duration_s"], base["hz4096"],
        ("%.3f×" % base["media_rate"]) if base["media_rate"] else "-"))
    print("-" * 108)
    for r in rows:
        if r is base:
            continue
        notes = []
        # ① 实测倍速（引擎日志）：墙钟每秒推进多少秒媒体
        if r["media_rate"] and r["speed"]:
            err = (r["media_rate"] - r["speed"]) / r["speed"] * 100.0
            notes.append("实测倍速 %.3f× vs 设定 %.2f×（偏差%+.1f%%）%s"
                         % (r["media_rate"], r["speed"], err, "✔" if abs(err) <= 10 else "✗"))
        # ② dump 时长：只有"把整片播到结尾"的跑法才应≈1.0×/倍速；固定墙钟窗口跑法两者本就相等
        if r["speed"] and base["speed"] and base["duration_s"]:
            exp = base["duration_s"] * base["speed"] / r["speed"]
            err = (r["duration_s"] - exp) / exp * 100.0
            same_wall = abs(r["duration_s"] - base["duration_s"]) <= max(0.5, 0.05 * base["duration_s"])
            if same_wall:
                notes.append("时长 %.3fs ≈ 基准（固定墙钟窗口跑法：dump 时长=录音时长，不参与倍速判定）" % r["duration_s"])
            else:
                notes.append("时长 期望≈%.3fs 实测%.3fs 偏差%+.1f%% %s"
                             % (exp, r["duration_s"], err, "✔" if abs(err) <= 10 else "✗"))
        # ③ 音高
        if r["hz4096"] and base["hz4096"]:
            d = (r["hz4096"] - base["hz4096"]) / base["hz4096"] * 100.0
            semis = 12 * math.log2(r["hz4096"] / base["hz4096"])
            ok = abs(semis) <= 1.0
            notes.append("基频 %s→%s Hz (%+.2f%% / %+.2f 半音) %s" % (
                base["hz4096"], r["hz4096"], d, semis, "✔ 保持音高" if ok else "✗ 疑似变调"))
        # ④ 静音/爆音/欠载
        if r["silence_count"] == 0:
            notes.append("无长静音 ✔")
        else:
            notes.append("静音段 %d 段(%.1fms) ⚠" % (r["silence_count"], r["silence_total_ms"]))
        if base["max_jump"] and r["max_jump"] > base["max_jump"] * 1.5:
            notes.append("最大跳变 %.3f→%.3f ⚠ 疑似爆音" % (base["max_jump"], r["max_jump"]))
        if r["underruns"] is not None and base["underruns"] is not None:
            notes.append("欠载 %s→%s %s" % (base["underruns"], r["underruns"],
                                           "✔" if r["underruns"] <= base["underruns"] + 2 else "⚠ 增加明显"))
        print("%-8s : %s" % (str(r["label"])[:8], "；".join(notes)))
    print("=" * 108)
    print("判读口径：")
    print("  ① 实测倍速（引擎日志 媒体位置增量÷墙钟）应≈设定倍速 ±10% —— 这是'倍速真的生效'的第一指标；")
    print("  ② dump 时长：**整片播到结尾**的跑法下 2.0× 应≈1.0× 的一半（±10%）；")
    print("     固定墙钟窗口的跑法下 dump 时长两边本就相等（声卡按实时速率出样本），别拿它判倍速；")
    print("  ③ 基频（FFT 主峰）应基本不变（±1 半音）= 没变调；")
    print("  ④ 长静音段 0 段、最大跳变不比 1.0× 明显变大（爆音）、欠载不显著增加。")


# ---------------------------------------------------------------- CLI
def main(argv=None):
    ap = argparse.ArgumentParser(description="分析播放器 audio_dump.f32（float32 交错），用于倍速验收")
    ap.add_argument("path", nargs="?", default=None, help="dump 文件路径（默认先找当前目录，再找播放器 bin 目录）")
    ap.add_argument("--rate", type=int, default=DEF_RATE, help="采样率（默认 48000）")
    ap.add_argument("--channels", type=int, default=DEF_CHANNELS, help="声道数（默认 2）")
    ap.add_argument("--silence-db", type=float, default=DEF_SILENCE_DB, help="静音阈值 dBFS（默认 -80）")
    ap.add_argument("--silence-min-ms", type=float, default=DEF_SILENCE_MIN_MS, help="静音段最短时长 ms（默认 5）")
    ap.add_argument("--max-list", type=int, default=DEF_MAX_LIST, help="静音段最多列出几条（默认 20）")
    ap.add_argument("--json", default=None, help="把本次结果写成 JSON（供 --compare 用）")
    ap.add_argument("--label", default=None, help="本次运行的标签，如 1.0x / 1.5x / 2.0x")
    ap.add_argument("--log", default=None, help="顺带解析 dsh_engine.log 里的'欠载 N 次'")
    ap.add_argument("--compare", nargs="+", default=None, help="对照模式：读多个 --json 结果出对照表")
    args = ap.parse_args(argv)

    if args.compare:
        compare(args.compare, args.max_list)
        return 0

    path = args.path or find_default_dump()
    rep = analyze(path, args.rate, args.channels, args.silence_db, args.silence_min_ms, args.max_list)
    if args.label:
        rep["label"] = args.label
    if args.log:
        rep["engine_log"] = parse_engine_log(args.log)
    print_report(rep, args.max_list)
    if args.log:
        print_log(rep.get("engine_log") or {})
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(rep, f, ensure_ascii=False, indent=2)
        print("JSON 已写入：%s" % os.path.abspath(args.json))
    return 0


if __name__ == "__main__":
    sys.exit(main())
