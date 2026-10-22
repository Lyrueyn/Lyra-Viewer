"""Node.js 运行时探测（给 yt-dlp 解 YouTube 的 JS 挑战用）。

★ 为什么需要：YouTube 对部分视频要求解 n/sig 挑战（n challenge），
   yt-dlp 需要真实 JavaScript 运行时 + yt-dlp-ejs 的求解脚本才能拿到完整格式列表。
   没有运行时 → 报 "n challenge solving failed" / 只有图片格式 / 强制 SABR。

探测顺序：
  1) <应用根>\runtime\node.exe（安装包自带，保证用户机器没装 Node 也能用）
  2) 系统 PATH 里的 node.exe（开发环境 / 自己装过 Node 的机器）
"""
from __future__ import annotations

import os
import shutil
from pathlib import Path
from typing import Optional

from .db import DATA_DIR


def find_node() -> Optional[str]:
    """返回可用的 node.exe 完整路径；找不到返回 None。"""
    try:
        bundled = DATA_DIR.parent / "runtime" / "node.exe"
        if bundled.exists():
            return str(bundled)
    except Exception:
        pass
    try:
        which = shutil.which("node")
        if which:
            return which
    except Exception:
        pass
    for cand in (
        r"C:\Program Files\nodejs\node.exe",
        r"C:\Program Files (x86)\nodejs\node.exe",
    ):
        try:
            if os.path.exists(cand):
                return cand
        except Exception:
            pass
    return None


def js_runtime_opts() -> dict:
    """给 yt-dlp 的 js_runtimes 配置（没有 node 时返回空 dict）。"""
    node = find_node()
    if not node:
        return {}
    return {"js_runtimes": {"node": {"path": node}}}