"""服务层：下载任务 / 增强任务 / 视频入库 / 查询（纯标准库，无框架依赖）。

HTTP 层（api.py fastapi 版、stdlib_server.py 标准库版）都调用本模块，
保证两套服务行为一致。
"""
from __future__ import annotations

import json
import logging
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from typing import Optional

from . import ai_deps, ai_pipeline, bilibili, ffmpeg_tools
from . import enhancers  # noqa: F401  导入即触发增强器注册
from .db import DATA_DIR, get_conn, init_db

logger = logging.getLogger(__name__)

DEFAULT_FORMAT = "bv*+ba/b"

CONFIG_FILE = DATA_DIR / "config.json"

# config.json 读写锁：后端是多线程 HTTP 服务，读-改-写必须原子，
# 否则并发请求（如弹幕设置保存 + cookie 保存同时发生）会互相覆盖丢失字段
_config_lock = threading.Lock()


def _proj_root() -> Path:
    """项目根目录（含 backend/ 与 data/ 的上级）。DATA_DIR = <root>/data。"""
    return DATA_DIR.parent


def _find_node() -> Optional[str]:
    """在本机定位 node.exe（Playwright 扫码脚本需要）。先后端 venv 旁、再 PATH。"""
    import shutil as _sh
    import os as _os
    cands = [
        r"D:\Program Files\nodejs\node.exe",
        r"C:\Program Files\nodejs\node.exe",
    ]
    for c in cands:
        if Path(c).exists():
            return c
    return _sh.which("node")

# 音频类增强器：对视频使用时走「抽音轨 → 增强 → 合回视频」流程
AUDIO_ENHANCERS = {"denoise", "loudness", "audio_sr", "soxr_hires", "fft_hires"}


def _find_ffmpeg() -> str:
    """统一解析（T24：FFMPEG_PATH → 常见位置 → PATH；详见 app/ffmpeg_tools.py）。

    ★ 保持旧语义：解析不到时返回裸名 "ffmpeg"（不是抛异常）——
      本函数的调用点里 `_video_duration_ms` 在 try **外面**调用它，
      一旦改成抛异常会把"列表页探测时长"整条路径带崩。
    """
    return ffmpeg_tools.find_ffmpeg(required=False) or "ffmpeg"


def _load_config() -> dict:
    if CONFIG_FILE.exists():
        try:
            return json.loads(CONFIG_FILE.read_text("utf-8"))
        except Exception:
            return {}
    return {}


def _save_config(cfg: dict) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), "utf-8")


def _update_config(mutator) -> dict:
    """原子更新 config.json：读-改-写整体持锁（多线程并发下不丢字段）。"""
    with _config_lock:
        cfg = _load_config()
        cfg = mutator(cfg)
        _save_config(cfg)
        return cfg


def get_cookie() -> str:
    return _load_config().get("cookie", "")


# 弹幕设置的默认值（与前端 DanmakuLayer 默认一致）
DANMAKU_SETTINGS_DEFAULTS = {
    "auto_reduce": True,
    "density_pct": 100,
    "max_active_enabled": False,
    "max_active": 50,
    "area_pct": 100,
    # ★ 滚动弹幕显示区域：只占视频最上 X%（100=全屏；顶部/底部固定弹幕不受影响）
    "scroll_area_pct": 100,
    "font_pct": 100,
    "speed_rate": 1.0,
    "dm_opacity": 1.0,
    "dm_visible": True,
}


# 画质排序（数值越大越高）——list_videos 分组选主条、list_video_records 排序都用它
_QUALITY_RANK = {
    "杜比视界": 8, "4K": 7, "1080P60": 6, "1080P+": 5, "1080P": 4,
    "720P": 3, "480P": 2, "360P": 1,
}

# ★ 画质等级查找（T21）：**复用上面那张唯一的 _QUALITY_RANK**，不再另造第二张表
#   （两张表以后必然漂移）。这里只是在它之上做「归一化索引 + 后缀容错」。
#   归一化索引由 _QUALITY_RANK 派生，改了上面那张表这里自动跟随。
_QUALITY_RANK_N = {str(k).strip().upper().replace(" ", ""): v for k, v in _QUALITY_RANK.items()}


def _quality_level(name: Optional[str]) -> int:
    """画质名 → 等级（未知 = 0）。容错："1080P+"、" 1080p "、"1080P60帧"、"2160P"、"1080"。

    ★ 直接查 _QUALITY_RANK：表里已有 "1080P+"/"1080P60" 两项，所以先归一化再查，
      不会被 strip()/大小写弄丢（"1080P+" → 命中表里的 "1080P+" = 5）。
    """
    if not name:
        return 0
    u = str(name).strip().upper().replace(" ", "")
    if not u:
        return 0
    if u in _QUALITY_RANK_N:
        return _QUALITY_RANK_N[u]
    if u.endswith("+"):                          # 表里没有的 "NNNP+" → 同高度档 +1
        base = _QUALITY_RANK_N.get(u[:-1])
        if base:
            return base + 1
    m = re.search(r"(\d{3,4})", u)               # "1080P60帧" / "2160P" / "1080"
    if m:
        h = int(m.group(1))
        # ★ 顺序要紧：先看后缀再退到基础档，且**只有字符串里真带 "60"/"+" 时才试对应档**
        #   （否则 "1080" 会被误判成 "1080P60"=6；"2160P" 里的 "60" 是数字本身的一部分，靠表查不到自然落空）
        if "60" in u:
            lv = _QUALITY_RANK_N.get("{}P60".format(h))
            if lv:
                return lv
        if "+" in u:
            lv = _QUALITY_RANK_N.get("{}P+".format(h))
            if lv:
                return lv
        lv = _QUALITY_RANK_N.get("{}P".format(h))
        if lv:
            return lv
        return {4320: 9, 2160: 8}.get(h, 0)      # 表里没有的高度：8K/4K 档
    return 0


def _qn_level(qn: int) -> int:
    """请求档位 qn → 等级（经 bilibili.QUALITY_NAMES 翻译；未知 = 0）。"""
    return _quality_level(bilibili.QUALITY_NAMES.get(qn, ""))


def collection_skip_decision(have_quality: Optional[str], req_qn: int,
                             avail_qns: Optional[list] = None,
                             bvid_max_owned: Optional[str] = None) -> dict:
    """合集分P「是否已下过」判定（纯函数，便于离线验证）。

    ★ T21 修的真 bug：以前用 `QUALITY_NAMES[qn]`（默认 qn=120 → "4K"）去和**库里存的画质字符串**
      做**精确相等**比较，而库里存的是**实际下到的流**的画质（源最高只有 1080P+ 时就是 "1080P+"）
      → 两边永不相等 → 跳过永不触发 → 整个合集（11P ≈1GB）每次都重下一遍。

    规则：
      1) 按**等级**比（复用 _QUALITY_RANK），不再比字符串：已有等级 >= 请求等级 → 已满足；
      2) **请求等级高于源可提供的最高等级时封顶**（默认请求 qn=120→"4K"=7、源只有 "1080P+"=5 是常态）
         → 按源最高档判定，已持有该档就算满足，不再每次重下；
      3) 拿不到源可用画质（离线/接口失败）时退化为：已有等级 >= 该 bvid **已下过的最高等级**
         → 视为满足（宁可保守不重下；一旦拿到可用画质信息，一律以信息为准）；
      4) **不过度跳过**：已有 "720P"(3)、要 "1080P+"(5) 且源支持 → 3 < 5 → 仍要下载。

    返回 {skip, have_level, req_level, eff_level, cap_name, avail_level, note}
    """
    have_lv = _quality_level(have_quality)
    req_name = bilibili.QUALITY_NAMES.get(req_qn or 0, "") or "任意画质"
    req_lv = _quality_level(req_name)
    avail_lv = max([_qn_level(q) for q in (avail_qns or [])] or [0])
    eff_lv, cap_name, note = req_lv, "", ""
    if avail_lv and req_lv > avail_lv:
        eff_lv = avail_lv
        cap_qn = next((q for q in (avail_qns or []) if _qn_level(q) == avail_lv), 0)
        cap_name = bilibili.QUALITY_NAMES.get(cap_qn, "") or str(cap_qn)
        note = "请求 {}（等级 {}）超过源最高 {}（等级 {}）→ 按 {} 判定".format(
            req_name, req_lv, cap_name, avail_lv, cap_name)
    skip = have_lv > 0 and have_lv >= eff_lv
    if not skip and not avail_lv and bvid_max_owned:
        own_lv = _quality_level(bvid_max_owned)
        if have_lv > 0 and have_lv >= own_lv:
            skip = True
            note = ("源可用画质未知 → 已有 {}（等级 {}）>= 该 bvid 已下过的最高等级 {}"
                    "（等级 {}）→ 视为已满足").format(have_quality, have_lv, bvid_max_owned, own_lv)
    return {"skip": skip, "have_level": have_lv, "req_level": req_lv, "eff_level": eff_lv,
            "cap_name": cap_name, "avail_level": avail_lv, "note": note}


def get_danmaku_settings() -> dict:
    """读弹幕设置（持久化在 config.json 的 danmaku_settings 字段）。"""
    cfg = _load_config().get("danmaku_settings") or {}
    merged = dict(DANMAKU_SETTINGS_DEFAULTS)
    merged.update({k: v for k, v in cfg.items() if k in DANMAKU_SETTINGS_DEFAULTS})
    return merged


def save_danmaku_settings(settings: dict) -> dict:
    """保存弹幕设置（只合并合法字段，忽略未知键）。原子更新不丢其它字段（如 cookie）。"""
    def mutate(cfg: dict) -> dict:
        cur = dict(DANMAKU_SETTINGS_DEFAULTS)
        cur.update(cfg.get("danmaku_settings") or {})
        for k, v in settings.items():
            if k in DANMAKU_SETTINGS_DEFAULTS:
                cur[k] = v
        cfg["danmaku_settings"] = cur
        return cfg
    return _update_config(mutate)


def get_cookie_detail() -> dict:
    """已存 Cookie 的详情：完整串 + 解析成 {name: value}（前端回填表格用）
    + 实时登录态（is_login/uname/is_vip，供前端显示当前是否真的登录）。"""
    c = get_cookie()
    pairs: dict[str, str] = {}
    for item in c.split("; "):
        if "=" in item:
            k, _, v = item.partition("=")
            pairs[k] = v
    bilibili.set_login_cookie(c)
    login = bilibili.check_login() if c else {}
    return {
        "has_cookie": bool(c),
        "cookie": c,
        "pairs": pairs,
        "is_login": login.get("is_login", False),
        "uname": login.get("uname", ""),
        "is_vip": login.get("is_vip", False),
        "vip_type": login.get("vip_type", 0),
    }


def _clean_cookie_value(v: str) -> str:
    """清洗 Cookie 值：只留可打印 ASCII（HTTP 头要求 latin-1，✓ 等会炸）。"""
    import re
    return re.sub(r"[^\x20-\x7E]", "", v or "").strip()


def _parse_cookie_text(text: str) -> str:
    """解析多行 Cookie 输入为 Cookie 串。

    支持两种格式：
    1) F12 表格粘贴（制表符分隔：Name\\tValue\\t...）→ 自动取 Name/Value
    2) 每行 Name=Value
    3) 单独一行值 → 视为 SESSDATA
    """
    pairs: list[str] = []
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line:
            continue
        if "\t" in line:
            parts = [p.strip() for p in line.split("\t")]
            if len(parts) >= 2 and parts[0] and parts[1] and parts[0] not in ("Name", "名字"):
                pairs.append("{}={}".format(_clean_cookie_value(parts[0]),
                                            _clean_cookie_value(parts[1])))
        elif "=" in line:
            k, _, v = line.partition("=")
            pairs.append("{}={}".format(_clean_cookie_value(k), _clean_cookie_value(v)))
        elif line:
            pairs.append("SESSDATA={}".format(_clean_cookie_value(line)))  # 单独值视为 SESSDATA
    return "; ".join(pairs)


def set_cookie(cookie: str) -> dict:
    """保存 B 站登录 Cookie（高清下载用），并热更新到 B 站模块。

    兼容：完整 Cookie 串 / F12 表格粘贴（多行）/ 只填 SESSDATA 值。
    保存后验证登录态，返回 is_login / 大会员状态 / 完整度。
    """
    c = _parse_cookie_text(cookie).strip()
    if c and "=" not in c:
        c = "SESSDATA=" + c
    _update_config(lambda cfg: {**cfg, "cookie": c})
    bilibili.set_login_cookie(c)
    login = bilibili.check_login() if c else {"is_login": False, "uname": "", "is_vip": False, "vip_type": 0}
    # 完整度检测：4K 需要 bili_jct / DedeUserID / buvid3 等（仅 SESSDATA 最高 1080p）
    c_lower = c.lower()
    complete = all(k in c_lower for k in ("bili_jct=", "dedeuserid=", "buvid3="))
    return {
        "ok": True,
        "has_cookie": bool(c),
        "is_login": login.get("is_login", False),
        "uname": login.get("uname", ""),
        "is_vip": login.get("is_vip", False),
        "vip_type": login.get("vip_type", 0),
        "cookie_complete": complete,
    }


def _extract_bvid(url: str) -> str:
    m = re.search(r"(BV[0-9A-Za-z]{10})", url)
    if m:
        return m.group(1)
    if url.startswith("BV"):
        return url
    raise ValueError("无法从链接中提取 BV 号")


def _extract_page(url: str) -> int:
    """提取 URL 里的分P序号 ?p=N（B站 1-based 语义），无则返回 0。"""
    m = re.search(r"[?&]p=(\d+)", url)
    return int(m.group(1)) if m else 0


def import_cookie_from_playwright() -> dict:
    """从 Playwright 抓取的 Cookie 文件导入登录态（无需手动填表格、无需手动敲命令）。

    若尚无 Cookie 文件：自动后台拉起 tools/bilibili_auto_login.mjs（会弹浏览器窗口，
    用户扫码/登录一次即可），然后轮询等待其生成 data/playwright_cookies.json 再解析导入。
    若已有 Cookie 文件：直接解析导入。
    文件格式：Playwright storageState (cookies: [{name,value,domain,...}] 数组)，
    或简单 {cookie: "SESSDATA=...; bili_jct=..."}。
    """
    import json as _json
    import subprocess as _sp
    import time as _time

    f = DATA_DIR / "playwright_cookies.json"
    if not f.exists():
        # 无 Cookie 文件 → 自动拉起扫码登录脚本（后台弹浏览器），并等待其产出文件
        script = _proj_root() / "tools" / "bilibili_auto_login.mjs"
        node = _find_node()
        if not script.exists():
            return {"ok": False, "error": "未找到扫码脚本 tools/bilibili_auto_login.mjs"}
        if not node:
            return {"ok": False, "error": "未找到 Node.js（Playwright 需要），请安装后重试"}
        try:
            _sp.Popen(
                [node, str(script)],
                cwd=str(_proj_root() / "tools"),
                stdout=_sp.DEVNULL, stderr=_sp.DEVNULL,
                creationflags=getattr(_sp, "CREATE_NEW_PROCESS_GROUP", 0) | 0x08000000,  # 独立后台
            )
        except Exception as e:
            return {"ok": False, "error": "启动扫码脚本失败: {}".format(e)}
        # 轮询等待文件生成（脚本最长要 120 秒扫码），给 130 秒
        wait_deadline = _time.time() + 130
        while _time.time() < wait_deadline:
            if f.exists():
                break
            _time.sleep(2)
        if not f.exists():
            return {"ok": False, "error": "等了一会未检测到登录（脚本可能未打开浏览器或登录超时），请重试"}

    try:
        data = _json.loads(f.read_text("utf-8"))
    except Exception as e:
        return {"ok": False, "error": "Cookie 文件解析失败: {}".format(e)}

    # 兼容两种格式：storageState({cookies:[...]}) 或直接 {cookie:"..."}
    if isinstance(data, dict) and data.get("cookie"):
        cookie_str = str(data["cookie"])
    else:
        cookies = data.get("cookies", []) if isinstance(data, dict) else []
        if not cookies and isinstance(data, list):
            cookies = data
        pairs = []
        for c in cookies:
            name = (c.get("name") or "").strip()
            val = (c.get("value") or "").strip()
            dom = (c.get("domain") or "")
            if name and val and (".bilibili.com" in dom or "bilibili" in dom):
                pairs.append("{}={}".format(_clean_cookie_value(name), _clean_cookie_value(val)))
        cookie_str = "; ".join(pairs)

    if not cookie_str:
        return {"ok": False, "error": "未找到 B 站登录 Cookie（脚本可能未登录）"}

    return set_cookie(cookie_str)

DOWNLOAD_DIR = DATA_DIR / "downloads"


def _env_dir(name: str, default: Path) -> Path:
    """目录可被环境变量覆盖（★ T27：测试隔离用）。

    例：`DSH_ENHANCED_DIR=D:\\DSH\\_tmp_t27\\enhanced` → 增强产物写到临时目录，
    **不污染用户的 data\\enhanced\\**。未设置时行为与以前完全一致（= DATA_DIR 下）。
    """
    raw = (os.environ.get(name, "") or "").strip().strip('"')
    if not raw:
        return default
    p = Path(raw)
    try:
        p.mkdir(parents=True, exist_ok=True)
    except Exception:
        logger.warning("%s=%r 无法创建，回落到默认目录 %s", name, raw, default)
        return default
    logger.info("%s 被环境变量覆盖 → %s（测试隔离）", name, p)
    return p


ENHANCED_DIR = _env_dir("DSH_ENHANCED_DIR", DATA_DIR / "enhanced")
COVER_DIR = DATA_DIR / "covers"      # ★ 视频封面（下载时一并抓下来，首页卡片显示）


def init_dirs() -> None:
    init_db()
    DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)
    ENHANCED_DIR.mkdir(parents=True, exist_ok=True)
    COVER_DIR.mkdir(parents=True, exist_ok=True)   # 封面目录
    # 启动时加载登录 Cookie（高清下载）
    bilibili.set_login_cookie(get_cookie())


def health() -> dict:
    return {"status": "ok", "service": "local-media-player-backend", "version": "0.1.0"}


def get_paths() -> dict:
    """本实例实际使用的目录（前端"打开下载文件夹"用）。"""
    return {
        "app_dir": str(_proj_root()),
        "data_dir": str(DATA_DIR),
        "download_dir": str(DOWNLOAD_DIR),
        "enhanced_dir": str(ENHANCED_DIR),
        "covers_dir": str(COVER_DIR),
    }


# ============ 下载任务 ============

def submit_download(url: str, format_sel: str = DEFAULT_FORMAT, qn: int = 120,
                    hi_res: bool = True, page: int = 0) -> dict:
    """提交下载任务（后台线程执行），返回 task_id。qn 为目标清晰度。hi_res 是否优选 Hi-Res 无损音频。page 分P序号(0=第一P)。

    ★ 2026-10-17（T15/A2-9）加两个保护（以前完全没有）：
      ① 同一 URL 已在队列中(pending/running) → **不再排一个重复任务**，直接返回原 task_id；
      ② 同一 bvid+分P 已有本地文件 → **仍然允许下载**（用户可能要另一种画质），
         但在返回值里带 `note` 提示，前端可提醒"这个分P已经下过了"。
    ★ 注意：以前 `INSERT INTO download_tasks (url, status)` 也不写 updated_at ——
      updated_at 是"任务是否还活着"的判据（僵尸任务自愈要用），这里一并写上。
    """
    url = (url or "").strip()
    if not url:
        return {"status": "error", "error": "url 为空"}
    bvid = _extract_bvid(url)
    with get_conn() as c:
        row = c.execute(
            "SELECT id FROM download_tasks WHERE url=? AND status IN ('pending','running') "
            "ORDER BY id DESC LIMIT 1",
            (url,),
        ).fetchone()
        if row:
            logger.info("重复提交（已在队列中）url=%s → task_id=%s", url, row["id"])
            return {"status": "queued_already", "task_id": int(row["id"])}
    note = ""
    if bvid:
        try:
            rec = get_video_record(bvid, page=page)
        except Exception:
            rec = None
        if rec:
            p = str(rec.get("local_path") or "").strip()
            if p and Path(p).exists():
                note = f"同一分P已有本地文件（{Path(p).name}）；如需别的画质可继续下载"
                logger.info("重复提交提示 bvid=%s page=%s 已有本地文件 → 仍按用户要求下载", bvid, page)
    with get_conn() as c:
        # ★ 2026-10-17：把下载参数一并存下来（qn/hi_res/page）——
        #   否则界面的"失败重试"只能拿默认值重新提交，用户特意选的低画质会被重下成高画质。
        cur = c.execute(
            "INSERT INTO download_tasks (url, status, updated_at, qn, hi_res, page) "
            "VALUES (?, 'pending', datetime('now','localtime'), ?, ?, ?)",
            (url, int(qn), 1 if hi_res else 0, int(page)),
        )
        task_id = int(cur.lastrowid)
    ev = _cancel_event(task_id)
    threading.Thread(
        target=_run_download, args=(task_id, url, format_sel, qn, hi_res, page),
        kwargs={"cancel_ev": ev}, daemon=True
    ).start()
    out = {"task_id": task_id}
    if note:
        out["note"] = note
    return out


def _extract_bvid(url: str) -> str:
    """从 URL 本地提取 BV 号（纯字符串匹配，不发网络请求 —— 提交接口不能被网络拖慢）。"""
    import re
    m = re.search(r"(BV[0-9A-Za-z]{10})", url or "")
    return m.group(1) if m else ""


def _detect_browser() -> Optional[str]:
    """检测本机已登录的浏览器（Chrome/Edge），供 yt-dlp 读取登录态。"""
    import os

    candidates = [
        ("chrome", os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Network\Cookies")),
        ("edge", os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Network\Cookies")),
    ]
    for name, path in candidates:
        if Path(path).exists():
            return name
    return None


class _DownloadCancelled(Exception):
    """用户取消下载的标记异常。"""


# 内存取消哨兵：cancel_download_task 立刻 set 对应 Event，下载线程无需等下一个 DB 查询即可感知。
# 内存 Event 与 DB download_tasks.cancel_requested 双保险——即使线程瞬时不查库，也能在下一进度点中断；
# 而持久化的 cancel_requested 保证后端重启后仍能识别"曾被取消"的任务。
_CANCEL_EVENTS: dict[int, threading.Event] = {}


def _cancel_event(task_id: int) -> threading.Event:
    ev = threading.Event()
    _CANCEL_EVENTS[task_id] = ev
    return ev


def _clear_cancel_event(task_id: int) -> None:
    _CANCEL_EVENTS.pop(task_id, None)


def _check_cancelled(task_id: int) -> None:
    """在下载关键点调用：若 cancel_requested 或内存哨兵命中，立即抛取消异常。"""
    ev = _CANCEL_EVENTS.get(task_id)
    if ev is not None and ev.is_set():
        raise _DownloadCancelled()
    with get_conn() as c:
        row = c.execute(
            "SELECT cancel_requested FROM download_tasks WHERE id=?", (task_id,)
        ).fetchone()
        if row and row["cancel_requested"]:
            raise _DownloadCancelled()


def cancel_download_task(task_id: int) -> dict:
    """请求取消下载：置 cancel_requested + set 内存哨兵，下载线程立即中断并标记 cancelled。

    若任务没有活跃线程（残留 running：进程重启后 thread 已死但 DB 仍是 running），
    直接就地标记为 cancelled，避免这个"假 running"永远卡在下载中。
    """
    ev = _CANCEL_EVENTS.get(task_id)
    if ev is None:
        # 无活跃下载线程 → 残留任务，直接标记取消
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='cancelled', cancel_requested=1 WHERE id=?",
                (task_id,),
            )
        return {"ok": True}
    with get_conn() as c:
        c.execute("UPDATE download_tasks SET cancel_requested=1 WHERE id=?", (task_id,))
    ev.set()
    return {"ok": True}


def delete_download_task(task_id: int) -> dict:
    """删除下载任务记录（保留可选：cancel 用 cancel_download_task）。"""
    with get_conn() as c:
        c.execute("DELETE FROM download_tasks WHERE id=?", (task_id,))
    return {"ok": True}


def clear_finished_download_tasks() -> dict:
    """清空所有"终态"下载任务记录：done / cancelled / failed（跳过 running/pending）。"""
    with get_conn() as c:
        cur = c.execute(
            "DELETE FROM download_tasks WHERE status IN ('done','cancelled','failed')"
        )
        n = cur.rowcount
    return {"ok": True, "deleted": n}


def mark_stale_running_tasks() -> dict:
    """启动时调用：上一个后端进程若异常退出，其下载线程已死但 DB 仍是 running/pending。
    把这些"假运行中"任务就地标记为 cancelled，避免用户看到永远不动的下载任务。"""
    with get_conn() as c:
        cur = c.execute(
            "UPDATE download_tasks SET status='cancelled' "
            "WHERE status IN ('running','pending')"
        )
        n = cur.rowcount
    if n:
        logger.info("清理 %d 个残留的运行中下载任务（上一进程已退出）", n)
    return {"ok": True, "marked": n}


def _run_download(task_id: int, url: str, format_sel: str, qn: int = 120,
                  hi_res: bool = True, page: int = 0, cancel_ev: Optional[threading.Event] = None) -> None:
    def progress(p: float) -> None:
        # 取消检查：内存哨兵（立即）优先，DB 兜底；命中即抛 _DownloadCancelled 中断下载
        if cancel_ev is not None and cancel_ev.is_set():
            raise _DownloadCancelled()
        with get_conn() as c:
            row = c.execute(
                "SELECT cancel_requested FROM download_tasks WHERE id=?", (task_id,)
            ).fetchone()
            if row and row["cancel_requested"]:
                raise _DownloadCancelled()
            c.execute(
                "UPDATE download_tasks SET progress=? WHERE id=?",
                (round(p * 100, 1), task_id),
            )

    try:
        with get_conn() as c:
            c.execute("UPDATE download_tasks SET status='running' WHERE id=?", (task_id,))
        # ★ 2026-10-14 关键修复：下载线程必须把登录 Cookie 装进 bilibili 模块。
        #   以前只有 parse/refresh 流程会 set_login_cookie，这里只判断了"配置文件里有没有 cookie"，
        #   于是 playurl 以**匿名身份**请求 → 最高只能拿 720P：用户选 4K/1080P，下回来还是 720P，
        #   文件名与入库画质同样写成 720P → 覆盖同一条记录 → 表现为"一个视频只能下一个画质"。
        bilibili.set_login_cookie(get_cookie())
        bvid = _extract_bvid(url)
        # ★ 2026-10-18：非 B 站链接（YouTube 等）走通用 yt-dlp 通道，直接下完入库。
        #   以前这里无条件按 B 站处理（bilibili.get_video_info(bvid)）→ 非 B 站必炸。
        if not bvid:
            logger.info("下载开始（非B站·通用通道）: %s  format_sel=%s", url, format_sel)
            info_out: dict = {}
            path = _download_via_ytdlp(url, format_sel, progress, info_out=info_out)
            title = str(info_out.get("title") or "") or path.stem
            height = int(info_out.get("height") or 0)
            quality = "{}P".format(height) if height > 0 else ""
            cover = _save_cover_generic(str(info_out.get("thumbnail") or ""), key=url)
            with get_conn() as c:
                c.execute("UPDATE download_tasks SET title=? WHERE id=?", (title, task_id))
            _video_to_db({
                "bvid": "",
                "title": title,
                "uploader": str(info_out.get("uploader") or ""),
                "duration": int(info_out.get("duration") or 0),   # 毫秒
                "description": str(info_out.get("description") or ""),
                "webpage_url": url,
                "cover_path": cover,
                "pub_time": int(info_out.get("pub_time") or 0),
                "subtitle_path": _find_subtitle_for(path),
            }, path, quality, 0)
            with get_conn() as c:
                c.execute(
                    "UPDATE download_tasks SET status='done', progress=100, local_path=? WHERE id=?",
                    (str(path), task_id),
                )
            return
        # ★ 记下真正的画质参数：qn<=0 表示"未指定"→ 后端会取最高那路（本例=杜比视界）。
        #   （曾经因为界面把 format_id 字符串读成 0，导致"选 480P 却下成杜比视界"，靠这行才查得出来。）
        logger.info("下载开始 qn=%s page=%s hi_res=%s → %s", qn, page, hi_res, url)
        if qn <= 0:
            logger.warning("qn 未指定（<=0）→ 将下载该视频可用的最高画质: %s", url)
        info = bilibili.get_video_info(bvid)
        cid = info["cid"]
        # 任务显示标题：视频名 + （合集分P名）
        task_title = info["title"]
        if page > 0 and (info.get("pages") or []):
            part = info["pages"][min(page - 1, len(info["pages"]) - 1)].get("part", "")
            if part and part != info["title"]:
                task_title = "{}（{}）".format(info["title"], part)
        with get_conn() as c:
            c.execute("UPDATE download_tasks SET title=? WHERE id=?", (task_title, task_id))
        # 多P：若 URL 指定 ?p=N 或前端传了 page，则用该分P的 cid
        if page > 0 and (info.get("pages") or []):
            cid = info["pages"][min(page - 1, len(info["pages"]) - 1)]["cid"]

        # 下载通道策略：
        # ① 有完整 Cookie → 零依赖通道（fourk=1 + wbi 风控参数，能拿大会员 4K/1080p+）
        # ② 无 Cookie → yt-dlp 免登录（1080p）
        # ③ 均失败 → 另一通道兜底
        if get_cookie():
            try:
                path, stream = bilibili.download_video(bvid, cid, DOWNLOAD_DIR, progress,
                                                       title=info["title"], qn=qn, hi_res=hi_res,
                                                       page=page)
                quality = bilibili.QUALITY_NAMES.get(stream["quality"],
                                                      "{}P".format(stream["height"] or ""))
                logger.info("零依赖通道下载完成（Cookie 高清 %s）", quality)
                # ★ 未达所选画质时明确告知（以前是静默降级 → 用户以为"下不了这个画质"）
                if qn and int(stream.get("quality") or 0) < qn:
                    want_note = bilibili.QUALITY_NAMES.get(qn, str(qn))
                    warn = ("未达所选画质：请求 {}，实际 {}（该分P无此档 / 未登录 / 大会员权限不足）"
                            .format(want_note, quality))
                    logger.warning("%s（%s）", warn, url)
                    with get_conn() as c:
                        c.execute("UPDATE download_tasks SET error=? WHERE id=?", (warn, task_id))
            except Exception as e:
                logger.warning("零依赖下载失败，改用 yt-dlp: %s", e)
                path = _download_via_ytdlp(url, format_sel, progress, qn)
                quality = ""
        else:
            path = _download_via_ytdlp(url, format_sel, progress, qn)
            quality = ""

        # ★ 必须先写视频库、再把任务标成"完成"：
        #   "完成"是给界面看的"可以刷新了"信号。反过来的话，前端一看到 done 就刷首页，
        #   而这时数据库里还没有这条视频 → 新下载的视频不会出现在首页
        #   （而且该任务 id 会被前端记入"已见过"，之后不会再触发刷新）。
        _vid_created = _video_to_db({
            "bvid": bvid,
            "title": info["title"],
            "uploader": info["owner"],
            "webpage_url": url,
            "pic": info.get("pic", ""),      # ★ 封面图 URL（下载时一并抓下来）
            "pub_time": int(info.get("pubdate") or 0),   # ★ 视频发布日期（B站 view.pubdate）
        }, path, quality, page)
        # ★ 顺带抓 B 站 CC 字幕（AI 字幕也能抓；失败不影响下载）
        try:
            save_bilibili_subtitles(_vid_created)
        except Exception as _se:
            logger.warning("下载后抓 B 站字幕失败: %s", _se)
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='done', progress=100, local_path=? WHERE id=?",
                (str(path), task_id),
            )
        # 弹幕入库（零依赖）
        _save_danmaku(str(path), bilibili.fetch_danmaku(cid))
    except _DownloadCancelled:
        logger.info("下载已取消 %s", url)
        with get_conn() as c:
            c.execute("UPDATE download_tasks SET status='cancelled' WHERE id=?", (task_id,))
    except Exception as e:
        logger.warning("下载失败 %s: %s", url, e)
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='failed', error=? WHERE id=?",
                (str(e), task_id),
            )
    finally:
        # 任务结束（无论成功/失败/取消）都释放内存哨兵，避免泄漏
        if cancel_ev is not None:
            _clear_cancel_event(task_id)


def submit_collection_download(url: str, format_sel: str = DEFAULT_FORMAT,
                               qn: int = 120, hi_res: bool = True) -> dict:
    """提交"下载整个合集"任务：B 站多P / YouTube 播放列表一样处理，并自动建本地「合集」。

    ★ 2026-10-19：以前只支持 B 站（bilibili.get_video_info）；非 B 站链接改走通用播放列表通道。
    """
    bvid = ""
    try:
        bvid = _extract_bvid(url)
    except Exception:
        bvid = ""
    with get_conn() as c:
        cur = c.execute(
            "INSERT INTO download_tasks (url, status) VALUES (?, 'running')", (url,)
        )
        task_id = int(cur.lastrowid)
    ev = _cancel_event(task_id)
    if bvid:
        threading.Thread(
            target=_run_collection_download, args=(task_id, url, format_sel, qn, hi_res),
            kwargs={"cancel_ev": ev}, daemon=True
        ).start()
    else:
        threading.Thread(
            target=_run_playlist_download, args=(task_id, url, format_sel),
            kwargs={"cancel_ev": ev}, daemon=True
        ).start()
    return {"task_id": task_id}


def _run_playlist_download(task_id: int, url: str, format_sel: str,
                           cancel_ev: Optional[threading.Event] = None) -> None:
    """后台：下载非 B 站播放列表（YouTube list=…）全部视频，逐个入库并加入本地「合集」。"""
    from . import downloader

    try:
        pl = downloader.parse_playlist(url, limit=500, cookiefile=get_youtube_cookie_file())
        entries = [e for e in (pl.get("entries") or []) if e.get("url")]
        if not entries:
            with get_conn() as c:
                c.execute("UPDATE download_tasks SET status='failed', error=? WHERE id=?",
                          ("播放列表解析不到视频", task_id))
            return
        for e in entries:
            e["format_sel"] = e.get("format_sel") or format_sel
        _download_playlist_entries(task_id, entries, (pl.get("title") or "").strip() or "YouTube 合集", cancel_ev)
    except Exception as e:
        logger.warning("播放列表下载失败: %s", e)
        with get_conn() as c:
            c.execute("UPDATE download_tasks SET status='failed', error=? WHERE id=?",
                      (str(e)[:300], task_id))


def submit_playlist_items(items: list, playlist_name: str = "") -> dict:
    """提交"下载播放列表选中条目"任务。

    items: [{"url": ..., "title": ..., "format_sel": ...}]（每条可单独指定画质）
    playlist_name: 本地合集名（空则用"YouTube 合集"）
    """
    clean = []
    for it in (items or []):
        if isinstance(it, dict) and str(it.get("url") or "").strip():
            clean.append({
                "url": str(it["url"]).strip(),
                "title": str(it.get("title") or ""),
                "format_sel": str(it.get("format_sel") or DEFAULT_FORMAT),
            })
    with get_conn() as c:
        cur = c.execute(
            "INSERT INTO download_tasks (url, status) VALUES (?, 'running')",
            (clean[0]["url"] if clean else "",),
        )
        task_id = int(cur.lastrowid)
    ev = _cancel_event(task_id)
    threading.Thread(
        target=_run_playlist_items, args=(task_id, clean, playlist_name),
        kwargs={"cancel_ev": ev}, daemon=True
    ).start()
    return {"task_id": task_id, "count": len(clean)}


def _run_playlist_items(task_id: int, items: list, playlist_name: str,
                        cancel_ev: Optional[threading.Event] = None) -> None:
    """后台：下载用户勾选的播放列表条目（每条自带 format_sel 画质）。"""
    try:
        if not items:
            with get_conn() as c:
                c.execute("UPDATE download_tasks SET status='failed', error=? WHERE id=?",
                          ("没有选中任何视频", task_id))
            return
        _download_playlist_entries(task_id, items, (playlist_name or "").strip() or "YouTube 合集", cancel_ev)
    except Exception as e:
        logger.warning("播放列表条目下载失败: %s", e)
        with get_conn() as c:
            c.execute("UPDATE download_tasks SET status='failed', error=? WHERE id=?",
                      (str(e)[:300], task_id))


def _download_playlist_entries(task_id: int, entries: list, pl_name: str,
                               cancel_ev: Optional[threading.Event] = None) -> None:
    """逐个下载播放列表条目（entries 每项含 url/title/format_sel），入库并加入本地同名合集。"""
    from . import playlists_db

    def set_status(status, error="", prog=None):
        with get_conn() as c:
            if prog is not None:
                c.execute("UPDATE download_tasks SET status=?, progress=?, error=? WHERE id=?",
                          (status, prog, error, task_id))
            else:
                c.execute("UPDATE download_tasks SET status=?, error=? WHERE id=?",
                          (status, error, task_id))

    total = len(entries)
    with get_conn() as c:
        c.execute("UPDATE download_tasks SET title=? WHERE id=?", (pl_name, task_id))
    # 复用同名本地合集，没有就新建
    playlist_id = 0
    try:
        for item in (playlists_db.list_playlists().get("playlists") or []):
            if (item.get("name") or "") == pl_name:
                playlist_id = int(item.get("id") or 0)
                break
    except Exception:
        pass
    if playlist_id <= 0:
        try:
            _resp = playlists_db.create_playlist(pl_name)
            playlist_id = int(_resp.get("playlist_id") or _resp.get("id") or 0)
        except Exception as e:
            logger.warning("创建本地合集失败: %s", e)
    logger.info("播放列表下载开始: %s（%d 个视频，合集 id=%s）", pl_name, total, playlist_id)
    ok = 0
    try:
        for i, e in enumerate(entries):
            if cancel_ev is not None and cancel_ev.is_set():
                raise _DownloadCancelled()
            item_fmt = str(e.get("format_sel") or DEFAULT_FORMAT)
            try:
                info_out: dict = {}
                path = _download_via_ytdlp(e["url"], item_fmt, lambda p: None, info_out=info_out)
                title = str(info_out.get("title") or "") or e.get("title") or path.stem
                height = int(info_out.get("height") or 0)
                quality = "{}P".format(height) if height > 0 else ""
                cover = _save_cover_generic(str(info_out.get("thumbnail") or ""), key=e["url"])
                vid = _video_to_db({
                    "bvid": "",
                    "title": title,
                    "uploader": str(info_out.get("uploader") or ""),
                    "duration": int(info_out.get("duration") or 0),
                    "description": str(info_out.get("description") or ""),
                    "webpage_url": e["url"],
                    "cover_path": cover,
                    "pub_time": int(info_out.get("pub_time") or 0),
                    "subtitle_path": _find_subtitle_for(path),
                }, path, quality, 0)
                if playlist_id > 0 and vid > 0:
                    try:
                        playlists_db.add_video_to_playlist(playlist_id, vid)
                    except Exception:
                        pass
                ok += 1
            except _DownloadCancelled:
                raise
            except Exception as ex:
                logger.warning("播放列表第 %d 个下载失败: %s", i + 1, str(ex)[:200])
            set_status("running", prog=round((i + 1) / total * 100, 1))
        set_status("done", prog=100.0)
        logger.info("播放列表下载完成: %s（成功 %d/%d，合集 id=%s）", pl_name, ok, total, playlist_id)
    except _DownloadCancelled:
        set_status("cancelled", "已取消")
    except Exception as e:
        logger.warning("播放列表下载失败: %s", e)
        set_status("failed", str(e)[:300])


def _run_collection_download(task_id: int, url: str, format_sel: str,
                             qn: int = 120, hi_res: bool = True,
                             cancel_ev: Optional[threading.Event] = None) -> None:
    """后台：遍历全部分P逐个下载入库，并把每个分P加进新建的本地「合集」歌单。"""
    def set_status(status, error="", prog=None):
        with get_conn() as c:
            if prog is not None:
                c.execute("UPDATE download_tasks SET status=?, progress=?, error=? WHERE id=?",
                          (status, prog, error, task_id))
            else:
                c.execute("UPDATE download_tasks SET status=?, error=? WHERE id=?",
                          (status, error, task_id))

    try:
        # ★ 与单视频下载同理：合集下载线程也必须装上登录 Cookie，否则每P都只有 720P
        bilibili.set_login_cookie(get_cookie())
        bvid = _extract_bvid(url)
        info = bilibili.get_video_info(bvid)
        pages = info.get("pages") or [{"cid": info["cid"], "page": 1, "part": info["title"]}]
        total = len(pages)
        req_qn = qn
        req_name = bilibili.QUALITY_NAMES.get(qn, "") or "任意画质"

        # ★ T21：源**可用画质**（给"请求超过源最高"封顶用）。只取一次；失败不影响下载，判定退化为按已有最高等级。
        avail_qns: Optional[list] = None
        try:
            avail_qns = [int(x["qn"]) for x in (bilibili.get_available_qualities(bvid, pages[0]["cid"]) or [])
                         if x.get("qn")]
            logger.info("合集画质：源可用 %s（请求 %s）",
                        ",".join(bilibili.QUALITY_NAMES.get(q, str(q)) for q in sorted(avail_qns, reverse=True)),
                        req_name)
        except Exception as e:
            logger.info("合集画质：取源可用画质失败（%s）→ 判定退化为按已有最高等级", e)

        # 该 bvid 已下过的最高等级（拿不到可用画质时的兜底参照）
        with get_conn() as c:
            owned_q = [r["quality"] for r in c.execute(
                "SELECT quality, local_path FROM videos WHERE bvid=?", (bvid,)).fetchall()
                if r["local_path"] and Path(r["local_path"]).exists()]
        bvid_max_owned = max(owned_q, key=_quality_level) if owned_q else ""

        # 建/复用一个本地「合集」歌单（同名复用，避免重复下载产生一堆重复歌单）
        coll_name = "{}（合集 {}P）".format(info["title"], total)
        with get_conn() as c:
            row = c.execute("SELECT id FROM playlists WHERE name=?", (coll_name,)).fetchone()
            if row:
                playlist_id = int(row["id"])
            else:
                cur = c.execute("INSERT INTO playlists (name) VALUES (?)", (coll_name,))
                playlist_id = int(cur.lastrowid)

        for i, pg in enumerate(pages):
            cid = pg["cid"]
            page = pg.get("page") or (i + 1)

            # 取消检查：每P开始前检查（内存哨兵 + DB 双保险）
            _check_cancelled(task_id)

            # 防傻：该分P的**这个画质**已下载过 → 跳过（复用已有记录），避免重复下载/覆盖。
            # ★ 2026-10-14：以前只按 (bvid, page) 判断，于是"下过 720P"会让整个分P 的其它画质也一起被跳过。
            # ★ 2026-10-17（T21）：以前拿"请求画质名"去和库里的画质字符串**精确相等**比，而库里存的是
            #   **实际下到的流**的画质（源最高只有 1080P+ 时就是 "1080P+"）→ 默认 qn=120("4K") 永不相等
            #   → 跳过永不触发，整个合集会重下一遍。现改为**等级比较**（复用 _QUALITY_RANK）+ 请求封顶。
            existing = get_video_record(bvid, page, None)      # 取该分P 画质**最高**的那条记录
            decision = None
            if existing and existing.get("has_file"):
                decision = collection_skip_decision(existing.get("quality"), qn,
                                                    avail_qns, bvid_max_owned)
                if decision["note"]:
                    logger.info("合集画质判定 P%d：%s", page, decision["note"])
            if decision and decision["skip"]:
                video_id = int(existing["id"])
                logger.info("合集跳过 P%d：已有 %s（等级 %d）>= 请求 %s（等级 %d）",
                            page, existing.get("quality"), decision["have_level"], req_name,
                            decision["eff_level"])
            else:
                if decision:
                    logger.info("合集需下载 P%d：已有 %s（等级 %d）< 请求 %s（等级 %d）",
                                page, existing.get("quality"), decision["have_level"], req_name,
                                decision["eff_level"])
                def cb(p, _i=i, _t=total):
                    # 每块进度回调都检查取消，命中即中断整个合集下载
                    _check_cancelled(task_id)
                    set_status("running", prog=round((_i + p) / _t * 100, 1))
                path, stream = bilibili.download_video(bvid, cid, DOWNLOAD_DIR, cb,
                                                       title=info["title"], qn=qn,
                                                       hi_res=hi_res, page=page)
                quality = bilibili.QUALITY_NAMES.get(stream["quality"],
                                                      "{}P".format(stream["height"] or ""))
                # 入库 → 拿 video_id → 加进合集歌单
                video_id = _video_to_db({
                    "bvid": bvid, "title": info["title"], "uploader": info["owner"],
                    "webpage_url": url,
                    "pic": info.get("pic", ""),   # ★ 封面图 URL（下载时一并抓下来）
                    "pub_time": int(info.get("pubdate") or 0),   # ★ 视频发布日期
                }, path, quality, page)
                try:
                    save_bilibili_subtitles(video_id)   # ★ 合集下载也顺带抓 B 站 CC 字幕
                except Exception:
                    pass
            with get_conn() as c:
                c.execute(
                    "INSERT OR REPLACE INTO playlist_items "
                    "(playlist_id, media_type, media_id, position) VALUES (?, 'video', ?, ?)",
                    (playlist_id, video_id, i + 1),
                )
            set_status("running", prog=round((i + 1) / total * 100, 1))
            logger.info("合集下载进度 %d/%d: %s", i + 1, total, page)

        set_status("done", prog=100.0)
    except _DownloadCancelled:
        logger.info("合集下载已取消 %s", url)
        set_status("cancelled")
    except Exception as e:
        logger.warning("合集下载失败 %s: %s", url, e)
        set_status("failed", error=str(e))
    finally:
        if cancel_ev is not None:
            _clear_cancel_event(task_id)


_QN_HEIGHT_MAP = {120: 2160, 116: 1080, 112: 2160, 80: 1080, 64: 720, 32: 480, 16: 360}


def _download_via_ytdlp(url: str, format_sel: str, progress, qn: Optional[int] = None, info_out: Optional[dict] = None):
    """yt-dlp 通道：优先 config Cookie，其次浏览器登录态，最后免登录。qn 限制最高画质。"""
    from . import downloader

    # ★ 用户没选具体格式（format_sel 空）→ 用"最佳画质"避免 playlist 逐条下载某条探测失败就下低清
    format_sel = (str(format_sel or "").strip()) or downloader.DEFAULT_FORMAT

    if qn:
        max_h = _QN_HEIGHT_MAP.get(qn, 2160)
        # 限制到所选画质（否则 yt-dlp 默认选最高清）
        format_sel = "bv*[height<={}]+ba/b".format(max_h)

    # ★ 只有 B 站链接才用 B 站 Cookie（以前不分站点，会把 B站 SESSDATA 发给 YouTube）
    is_bili = bool(_extract_bvid(url))
    cookie_str = get_cookie() if is_bili else ""
    if cookie_str:
        return downloader.download(url, DOWNLOAD_DIR, format_sel, progress,
                                   cookies=cookie_str)
    if not is_bili:
        # 非 B 站（YouTube 等）：用「YouTube 登录窗口」抓来的标准 cookie 文件；没有就免登录下
        return downloader.download(url, DOWNLOAD_DIR, format_sel, progress,
                                   cookiefile=get_youtube_cookie_file(), info_out=info_out)
    browser = _detect_browser()
    try:
        return downloader.download(url, DOWNLOAD_DIR, format_sel, progress,
                                   cookies_from_browser=browser)
    except Exception as e:
        if browser and "cookie" in str(e).lower():
            logger.warning("浏览器 cookie 读取失败（浏览器运行中？），免登录重试: %s", e)
            return downloader.download(url, DOWNLOAD_DIR, format_sel, progress,
                                       cookies_from_browser=None)
        raise


def _download_with_ytdlp(url: str, format_sel: str, progress):
    from . import downloader  # 懒加载
    return downloader.download(url, DOWNLOAD_DIR, format_sel, progress)


def _video_to_db(info: dict, path: Path, quality: str = "", page: int = 0) -> int:
    """把下载的视频写入库（同视频同画质同分P则更新，否则新增），返回 video_id。"""
    bvid = info.get("bvid", "")
    title = info.get("title", path.stem)
    up = info.get("uploader", "")
    # desc：B 站存 webpage_url（简介另走 intro 接口）；非 B 站优先用 yt-dlp 给的简介
    desc = str(info.get("desc") or info.get("description") or info.get("webpage_url", "") or "")
    duration = int(info.get("duration", 0) or 0)   # 毫秒（调用方负责换算）
    pub_time = int(info.get("pub_time", 0) or 0)   # ★ 视频发布日期（Unix 秒）
    subtitle_path = str(info.get("subtitle_path") or "")   # ★ 字幕文件（.srt）
    # ★ 封面：调用方给了 cover_path（非 B 站用缩略图下载）优先；其次沿用本地已有；最后按 pic 现抓
    cover = str(info.get("cover_path") or "")
    if bvid:
        dst = _cover_path_for(bvid, page)
        if dst.exists():
            cover = str(dst)
        elif info.get("pic"):
            cover = _save_cover(bvid, page, info["pic"])
    with get_conn() as c:
        row = c.execute(
            "SELECT id FROM videos WHERE bvid=? AND quality=? AND page=? AND bvid!=''",
            (bvid, quality, page),
        ).fetchone()
        if row:
            # 同视频同画质同分P：更新本地文件路径（封面/字幕只在拿到新的才覆盖，避免把已有清空）
            c.execute(
                "UPDATE videos SET title=?, up_name=?, desc=?, local_path=?, duration=?, "
                "pub_time=CASE WHEN ? > 0 THEN ? ELSE pub_time END, "
                "cover_path=CASE WHEN ?<>'' THEN ? ELSE cover_path END, "
                "subtitle_path=CASE WHEN ?<>'' THEN ? ELSE subtitle_path END WHERE id=?",
                (title, up, desc, str(path), duration, pub_time, pub_time,
                 cover, cover, subtitle_path, subtitle_path, row["id"]),
            )
            return int(row["id"])
        # 不同画质/分P：新增一条记录（同一视频多画质/多分P共存）
        cur = c.execute(
            "INSERT INTO videos (bvid, title, up_name, desc, local_path, quality, page, duration, "
            "cover_path, pub_time, subtitle_path) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (bvid, title, up, desc, str(path), quality, page, duration, cover, pub_time, subtitle_path),
        )
        return int(cur.lastrowid)



def video_stats(video_id: int) -> dict:
    """视频的 B站播放/弹幕统计 + 发布日期（本地无记录则查 B站；无 bvid=本地视频返回 0）。

    ★ pub_time：B 站视频顺手从 view.pubdate 回填（老记录没有发布日期 → 界面只能显示下载时间）。
    """
    with get_conn() as c:
        row = c.execute("SELECT bvid, pub_time FROM videos WHERE id=?", (video_id,)).fetchone()
    if not row:
        return {"view": 0, "danmaku": 0, "pub_time": 0}
    pub = int(row["pub_time"] or 0)
    if not row["bvid"]:
        return {"view": 0, "danmaku": 0, "pub_time": pub}
    try:
        info = bilibili.get_video_info(row["bvid"])
        st = info.get("stat") or {}
        p = int(info.get("pubdate") or 0)
        if p > 0:
            pub = p
            if int(row["pub_time"] or 0) <= 0:
                try:
                    with get_conn() as c:
                        c.execute("UPDATE videos SET pub_time=? WHERE id=?", (p, video_id))
                    logger.info("发布日期回填: video_id=%s → %s", video_id, p)
                except Exception:
                    pass
        return {"view": int(st.get("view", 0) or 0), "danmaku": int(st.get("danmaku", 0) or 0), "pub_time": pub}
    except Exception:
        return {"view": 0, "danmaku": 0, "pub_time": pub}


def _save_danmaku(local_path: str, danmaku: list[dict]) -> None:
    """把整批弹幕写入 DB（这批就是该视频的完整弹幕 → **先清后写**）。

    ★ 2026-10-14 修：seg.so 接口不返回弹幕 id（dm_id 为空），而唯一索引带
      `WHERE dm_id IS NOT NULL` 条件、约束不到这些行 → 以前每次重新下载都整批追加一份
      （实测杜比演示片被下载 9 次：9621 行 / 去重仅 1066 行，画面上每条弹幕出现 9 遍）。
      现在改成"先删该视频无 id 的旧记录（保留刷新抓到的带 id 数据）再整批写"，
      并对本批做内容去重；db.py 里另有 idx_danmaku_noid 部分唯一索引兜底。
    """
    with get_conn() as c:
        row = c.execute(
            "SELECT id FROM videos WHERE local_path=? ORDER BY id DESC LIMIT 1",
            (local_path,),
        ).fetchone()
        if not row:
            return
        vid = row["id"]
        c.execute("DELETE FROM danmaku WHERE video_id=? AND dm_id IS NULL", (vid,))
        seen: set = set()
        for d in danmaku:
            key = (d["time"], d["mode"], d["size"], d["color"], d["text"])
            if key in seen:
                continue
            seen.add(key)
            c.execute(
                "INSERT OR IGNORE INTO danmaku (video_id, time, mode, size, color, text) "
                "VALUES (?, ?, ?, ?, ?, ?)",
                (vid, d["time"], d["mode"], d["size"], d["color"], d["text"]),
            )


def _cover_path_for(bvid: str, page: int = 0) -> Path:
    """封面的本地路径：data/covers/<BV>.jpg。

    ★ 同一视频的各分P/各画质共用一张（B站 view 接口给的 pic 是整个视频的封面，不是每P一张），
      所以**按视频存一份**即可 —— 以前按分P 命名会把同一张图存 11 份（实测每张还 4MB）。
    """
    return COVER_DIR / "{}.jpg".format(bvid)


def _srt_from_bili_body(body: list) -> str:
    """B站 CC 字幕 JSON body → SRT 文本。"""
    def _fmt(t: float) -> str:
        ms = int(round(float(t or 0) * 1000))
        h, ms = divmod(ms, 3600000)
        m, ms = divmod(ms, 60000)
        s, ms = divmod(ms, 1000)
        return "%02d:%02d:%02d,%03d" % (h, m, s, ms)
    lines = []
    idx = 0
    for item in body or []:
        content = str(item.get("content") or "").strip()
        if not content:
            continue
        idx += 1
        lines.append(str(idx))
        lines.append("%s --> %s" % (_fmt(item.get("from")), _fmt(item.get("to"))))
        lines.append(content)
        lines.append("")
    return "\n".join(lines)


def _srt_stats(text: str) -> tuple:
    """返回 (字幕条数, 正文字符数)，用来比较两份字幕的完整度。"""
    try:
        n = text.count("-->")
        chars = 0
        for line in text.splitlines():
            s = line.strip()
            if not s or "-->" in s or s.isdigit():
                continue
            chars += len(s)
        return n, chars
    except Exception:
        return 0, 0


def _read_srt_stats(p: Path) -> tuple:
    try:
        return _srt_stats(p.read_text(encoding="utf-8", errors="replace"))
    except Exception:
        return 0, 0


def save_bilibili_subtitles(video_id: int) -> dict:
    """下载该 B 站视频的全部 CC 字幕到视频旁（<视频名>.<语言>.srt；AI 字幕加 .auto. 标记），并更新 DB。

    ★ B 站接口不稳定（实测）：/x/player/v2 有时返回字幕列表但 subtitle_url 为空、有时给的是
      不同版本的 AI 字幕（CDN 快照完整度不同），导致「每次重新下载结果都不一样」。
    处理：
      · 同一次请求里重试最多 8 轮，直到拿到非空 url（url 为空时稍等再请求）；
      · 同一语言按「正文字符数最多」选最佳版本（段数只作次指标，AI 分段数不可靠）；
      · 正文短于 min_chars（视频时长推算）的版本视为 AI 还在生成/残缺，直接丢弃；
      · 磁盘上已有更完整文件 → 保留不覆盖；只有新版本明显更完整（字符数 +5% 以上）才替换。
    """
    import time as _time
    with get_conn() as c:
        row = c.execute("SELECT id, bvid, local_path FROM videos WHERE id=?", (video_id,)).fetchone()
    if not row or not row["bvid"]:
        return {"ok": False, "error": "不是 B 站视频"}
    path = Path(row["local_path"] or "")
    if not path.exists():
        return {"ok": False, "error": "本地文件不存在"}
    try:
        bilibili.set_login_cookie(get_cookie())   # ★ 没 Cookie 时 /x/player/v2 不返回字幕列表
        info = bilibili.get_video_info(row["bvid"])
        aid = info.get("aid") or 0
        cid = info.get("cid") or 0
        duration = int(info.get("duration") or 0)
        if not cid:
            return {"ok": False, "error": "拿不到 cid"}
        min_chars = max(200, int(duration * 0.15))   # 1037s 的视频至少 ~156 字，低于此视为残缺
        best = {}          # fname -> {text, chars, segs, manual, score}
        for _attempt in range(8):
            subs = bilibili.get_player_subtitles(row["bvid"], cid)
            got_url = False
            for s in subs:
                u = (s.get("url") or "").strip()
                if not u:
                    continue
                got_url = True
                lan = (s.get("lan") or "zh").strip()
                _ai_hint = lan.startswith("ai-") or int(s.get("ai_type") or 0) > 0
                # ★ B站实测会返回**别的视频**的 AI 字幕 URL（同一 aid/cid 请求下随机混入）。
                #   正确 URL 一定带本视频的 aid+cid（如 16563974501620074048...），否则丢弃。
                if _ai_hint and aid and cid and not (str(aid) in u and str(cid) in u):
                    logger.warning("B站字幕: 丢弃错绑 URL（不含 aid/cid）lan=%s url=%s", lan, u[:140])
                    continue
                body = bilibili.fetch_subtitle_body(u)
                if not body:
                    continue
                text = _srt_from_bili_body(body)
                segs, chars = _srt_stats(text)
                if chars < min_chars:
                    logger.info("B站字幕: 跳过过短版本 %s（%d 字 < %d）", lan, chars, min_chars)
                    continue
                ai = lan.startswith("ai-") or int(s.get("ai_type") or 0) > 0
                code = lan[3:] if lan.startswith("ai-") else lan
                fname = "{}{}.{}.srt".format(path.stem, ".auto" if ai else "", code)
                score = (1 if ai else 0, 0 if code.lower().startswith("zh") else 1, -chars, -segs)
                cur = best.get(fname)
                if cur is None or score < cur["score"]:
                    best[fname] = {"text": text, "chars": chars, "segs": segs,
                                   "manual": not ai, "ai": ai, "lan": lan, "url": u,
                                   "score": score}
            if best and got_url and len(best) >= 2:
                break
            if not subs or not got_url:
                _time.sleep(1.0)    # 空列表/url 未就绪：B站偶尔要等一会儿才给地址
                continue
            if best:
                break
        if not best:
            logger.info("B站字幕：多次请求均未返回可用字幕 video_id=%s", video_id)
            return {"ok": True, "count": 0, "files": [], "kept_count": 0,
                    "note": "B站接口这次没有返回可用字幕（AI字幕可能还在生成，稍后再试）"}
        saved, kept = [], []
        # ★ 来源清单：只对「清单里记录过、确认是本站视频抓来的文件」做“保留更完整版本”比较；
        #   旧版本/来源不明的文件一律用本次校验过的内容覆盖（防止随机错绑字幕被当成“更完整”永远保留）。
        meta_path = path.parent / (path.stem + ".subtitles_meta.json")
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else {}
        except Exception:
            meta = {}
        if not isinstance(meta, dict):
            meta = {}
        for fname, item in sorted(best.items()):
            dst = path.parent / fname
            old_segs, old_chars = _read_srt_stats(dst)
            known = fname in meta
            if old_chars > 0 and known:
                if item["manual"]:
                    better = item["chars"] >= max(min_chars, old_chars * 0.8)
                else:
                    better = item["chars"] >= max(min_chars, old_chars * 1.05)
                if not better:
                    kept.append(fname)
                    continue
            try:
                dst.write_text(item["text"], encoding="utf-8")
                saved.append(fname)
                meta[fname] = {
                    "bvid": row["bvid"], "aid": aid, "cid": cid,
                    "lan": item.get("lan") or "", "ai": bool(item.get("ai")),
                    "chars": item["chars"], "segs": item["segs"],
                    "url": (item.get("url") or "")[:300],
                    "at": time.strftime("%Y-%m-%d %H:%M:%S"),
                }
            except Exception as e:
                logger.warning("字幕写入失败 %s: %s", fname, e)
        for _k in list(meta.keys()):
            if not (path.parent / _k).exists():
                meta.pop(_k, None)
        try:
            if meta:
                meta_path.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
        except Exception as _e:
            logger.warning("字幕来源清单写入失败: %s", _e)
        if saved:
            # ★ 把旧的、不是本次 saved/kept 的字幕文件先备份再移走，避免错绑字幕一直留在语言列表
            try:
                import shutil as _shutil
                _keep = set(saved) | set(kept)
                _bak = DATA_DIR / "subtitle_backup" / time.strftime("%Y%m%d_%H%M%S")
                _moved = []
                for _f in path.parent.iterdir():
                    if not _f.is_file() or not _f.name.startswith(path.stem):
                        continue
                    if _f.suffix.lower() not in (".srt", ".vtt", ".ass", ".ssa"):
                        continue
                    if ".ai." in _f.name:      # Whisper 生成的字幕保留
                        continue
                    if _f.name in _keep:
                        continue
                    _bak.mkdir(parents=True, exist_ok=True)
                    _shutil.copy2(_f, _bak / _f.name)
                    _f.unlink()
                    _moved.append(_f.name)
                if _moved:
                    logger.info("清理旧字幕（备份到 %s）：%s", _bak, _moved)
            except Exception as _e:
                logger.warning("清理旧字幕失败: %s", _e)
            best_path = _find_subtitle_for(path)
            with get_conn() as c:
                c.execute("UPDATE videos SET subtitle_path=? WHERE id=?", (best_path, video_id))
        logger.info("B站字幕下载完成 video_id=%s：saved=%s kept=%s", video_id, saved, kept)
        return {"ok": True, "count": len(saved), "files": saved,
                "kept_count": len(kept), "kept": kept,
                "source_bvid": row["bvid"], "source_title": (info.get("title") or "")[:80],
                "cid": cid}
    except Exception as e:
        logger.warning("B站字幕下载失败 video_id=%s: %s", video_id, e)
        return {"ok": False, "error": str(e)[:200]}


def _norm_title(s: str) -> str:
    """标题归一化：去空白/标点/大小写，方便跟 download_tasks.title 做模糊匹配。"""
    import re as _re
    s = (s or "").lower()
    s = _re.sub(r"[\s\-_()（）\[\]【】.]+", "", s)
    return s


def _find_source_url_for(row) -> str:
    """为视频找回原始下载 URL。

    优先 local_path 精确匹配；再按「标题前缀/高相似度」匹配（YouTube 任务常因改名导致
    local_path 为空或不一致，但 download_tasks.title 和 videos.title 很接近）。
    """
    local = (row["local_path"] or "").strip()
    title = (row["title"] or "").strip()
    nt = _norm_title(title)
    best, best_score = "", 0
    try:
        with get_conn() as c:
            tasks = c.execute(
                "SELECT url, local_path, title FROM download_tasks "
                "WHERE url LIKE 'http%' AND status='done' ORDER BY id DESC LIMIT 200"
            ).fetchall()
    except Exception:
        return ""
    for t in tasks:
        url = (t["url"] or "").strip()
        if not url:
            continue
        lp = (t["local_path"] or "").strip()
        score = 0
        if local and lp:
            if lp.replace("/", "\\").lower() == local.replace("/", "\\").lower():
                score = 100
            elif local.lower().startswith(lp.lower()) or lp.lower().startswith(local.lower()):
                score = 90
        if score == 0:
            tt = _norm_title(t["title"] or "")
            if tt and nt:
                if tt == nt:
                    score = 80
                elif (len(tt) >= 8 and nt.startswith(tt)) or (len(nt) >= 8 and tt.startswith(nt)):
                    score = 70
                else:
                    # 粗略相似度：共同前缀比例
                    n = 0
                    for a, b in zip(tt, nt):
                        if a != b:
                            break
                        n += 1
                    if n >= 10 and n / max(len(tt), len(nt)) >= 0.55:
                        score = 50
        if score > best_score:
            best, best_score = url, score
    return best


def backfill_subtitles(video_id: int) -> dict:
    """给已下载的视频补字幕：B 站走 CC 接口，非 B 站（YouTube 等）走 yt-dlp（需要库里能查到原始链接）。"""
    import json as _json
    with get_conn() as c:
        row = c.execute("SELECT id, bvid, title, local_path FROM videos WHERE id=?", (video_id,)).fetchone()
    if not row:
        return {"ok": False, "error": "视频不存在"}
    if row["bvid"]:
        return save_bilibili_subtitles(video_id)
    # 非 B 站：从 download_tasks 里找原始 url
    # ★ 不能只按 local_path 精确匹配：YouTube 下载任务经常 local_path 为空（或最终文件名被改名），
    #   但 title 与 videos.title 很接近 → 用标题前缀/相似度回退找 URL。
    url = _find_source_url_for(row)
    if not url:
        return {"ok": False, "error": "找不到原始链接（无法补字幕）"}
    path = Path(row["local_path"] or "")
    if not path.exists():
        return {"ok": False, "error": "本地文件不存在"}
    try:
        from . import downloader
        res = downloader.fetch_yt_subtitles(url, path)
        best = _find_subtitle_for(path)
        with get_conn() as c:
            c.execute("UPDATE videos SET subtitle_path=? WHERE id=?", (best, video_id))
        return {"ok": True, "count": res.get("manual", 0) + res.get("auto", 0), "detail": res}
    except Exception as e:
        return {"ok": False, "error": str(e)[:200]}

def _find_subtitle_for(path: Path) -> str:
    """找与视频同名的字幕文件（下载时 yt-dlp 存成 <视频名>.<语言>.srt），优先中文。"""
    try:
        stem = path.stem
        # ★ 不能用 glob：视频名常带 [4K] / [1080P+] 这类方括号，glob 会把 [] 当字符类 → 永远匹配不到
        cands = []
        try:
            for fn in os.listdir(str(path.parent)):
                if not fn.startswith(stem):
                    continue
                low = fn.lower()
                if low.endswith((".srt", ".vtt", ".ass", ".ssa")):
                    cands.append(path.parent / fn)
        except Exception:
            return ""
        # ★ Whisper 的 .ai.srt 只属于「AI 字幕模式」，不能当成普通字幕的默认首选
        cands = [c for c in cands if ".ai." not in c.name.lower()]
        cands = sorted(cands)
        if not cands:
            return ""

        def _score(p: Path) -> tuple:
            n = p.name.lower()
            # 优先级：人工字幕 > 自动字幕；同类型里中文 > 其它
            auto = 1 if ".auto." in n else 0
            zh = 0 if any(k in n for k in ("zh", "chs", "cht", "chi", "chinese", "中文")) else 1
            return (auto, zh, len(n))

        best = sorted(cands, key=_score)[0]
        return str(best)
    except Exception:
        return ""


def _save_cover_generic(pic_url: str, key: str = "") -> str:
    """下载非 B 站（YouTube 等）视频封面，存到 covers/local_<hash>.jpg，返回本地路径（失败返回空串）。"""
    if not pic_url:
        return ""
    try:
        import hashlib
        import urllib.request as _ur
        base = pic_url.split("@")[0]
        if base.startswith("//"):
            base = "https:" + base
        req = _ur.Request(base, headers={
            "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                           "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"),
        })
        with _ur.urlopen(req, timeout=30) as r:
            data = r.read()
        if not data or len(data) < 512:
            return ""
        h = hashlib.sha1((key or base).encode("utf-8", "ignore")).hexdigest()[:16]
        COVER_DIR.mkdir(parents=True, exist_ok=True)
        dst = COVER_DIR / ("local_" + h + ".jpg")
        tmp = dst.with_suffix(".tmp")
        tmp.write_bytes(data)
        tmp.replace(dst)
        logger.info("封面已保存（非B站）%s（%d 字节）", dst.name, len(data))
        return str(dst)
    except Exception as e:
        logger.warning("非B站封面保存失败: %s", e)
        return ""

def _save_cover(bvid: str, page: int, pic_url: str) -> str:
    """下载视频封面（让 B站图床缩到 480×270，几十 KB），返回本地路径（失败返回空串）。

    ★ 2026-10-14：以前压根没存封面，首页卡片只能显示"占位色块 + 标题文字"。
    """
    if not bvid or not pic_url:
        return ""
    base = pic_url.split("@")[0]        # 去掉 B站原有的缩放/格式后缀
    if base.startswith("//"):
        base = "https:" + base
    elif base.startswith("http://"):    # 统一走 https（B站图床都支持）
        base = "https://" + base[len("http://"):]
    for url in (base, base + "@480w_270h_1c.jpg"):   # 优先原图（用户要求）；失败再退到缩略图
        try:
            data = bilibili._http_get(url, timeout=30)
            if not data or len(data) < 512:
                continue
            COVER_DIR.mkdir(parents=True, exist_ok=True)
            dst = _cover_path_for(bvid)
            tmp = dst.with_suffix(".tmp")
            tmp.write_bytes(data)
            tmp.replace(dst)
            logger.info("封面已保存 %s（%d 字节）", dst.name, len(data))
            return str(dst)
        except Exception as e:
            logger.warning("封面下载失败 %s: %s", url, e)
    return ""


# 历史视频补封面：节流（5 分钟一次）+ 每次最多几张，避免拖慢首页
_cover_backfill_lock = threading.Lock()
_cover_backfill_last = 0.0


def _backfill_covers(limit: int = 6) -> None:
    """后台给"还没有封面"的老记录补封面（按 bvid 查 view 接口拿 pic）。"""
    global _cover_backfill_last
    import time as _time
    with _cover_backfill_lock:
        if _time.time() - _cover_backfill_last < 300:
            return
        _cover_backfill_last = _time.time()
    try:
        with get_conn() as c:
            rows = c.execute(
                "SELECT DISTINCT bvid, page FROM videos "
                "WHERE bvid!='' AND (cover_path IS NULL OR cover_path='') LIMIT ?",
                (limit,),
            ).fetchall()
        for r in rows:
            try:
                info = bilibili.get_video_info(r["bvid"])
                cp = _save_cover(r["bvid"], r["page"] or 0, info.get("pic", ""))
                if cp:
                    with get_conn() as c:
                        c.execute("UPDATE videos SET cover_path=? WHERE bvid=? AND page=?",
                                  (cp, r["bvid"], r["page"] or 0))
            except Exception as e:
                logger.warning("补封面失败 %s: %s", r["bvid"], e)
    except Exception as e:
        logger.warning("补封面任务失败: %s", e)


# ★ 2026-10-17（T15/A2-9）：僵尸任务自愈
ZOMBIE_STALE_MIN = 10      # 超过这个分钟数没有任何更新 → 判定为"进程中断"


def sweep_zombie_tasks(stale_min: int = ZOMBIE_STALE_MIN) -> int:
    """把长时间没有进度更新的 pending/running 任务标成 failed。

    来源：下载过程中被强杀进程（本机历史上发生过多次）→ 任务永远停在 running，
    界面永远转圈，用户不知道它已经死了。`updated_at` 由 db.py 里的触发器自动维护
    （任何 UPDATE 都会刷新它），因此"多久没动"就是"这个任务还活着吗"的判据。
    幂等：只动"超时且仍在跑"的行，返回清理条数。
    """
    with get_conn() as c:
        cur = c.execute(
            "UPDATE download_tasks SET status='failed', "
            "error=COALESCE(NULLIF(error,''), ?), reason=?, "
            "updated_at=datetime('now','localtime') "
            "WHERE status IN ('pending','running') AND "
            "COALESCE(updated_at, created_at, datetime('now','localtime')) "
            "< datetime('now','localtime', ?)",
            ("进程中断", "进程中断（超过 %d 分钟无进度更新）" % stale_min,
             "-%d minutes" % stale_min),
        )
        n = cur.rowcount
    if n:
        logger.warning("僵尸任务自愈: %d 个 pending/running 超过 %d 分钟无更新 → 已标记 failed",
                       n, stale_min)
    return n


def list_download_tasks() -> dict:
    """下载任务列表（最新 50 条，含 reason/updated_at）。

    ★ 查询前先做一次僵尸自愈：进程被杀留下的 pending/running 会永远转圈，
    这里顺手把它们标成 failed，前端就能显示"失败"，而不是让用户一直等。
    """
    try:
        sweep_zombie_tasks()
    except Exception as e:
        logger.warning("僵尸任务自愈失败（忽略，不影响列表）: %s", e)
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, url, title, status, progress, local_path, error, reason, "
            "qn, hi_res, page, "
            "created_at, updated_at "
            "FROM download_tasks ORDER BY id DESC LIMIT 50"
        ).fetchall()
    return {"tasks": [dict(r) for r in rows]}


def delete_video(video_id: int, delete_file: bool = True) -> dict:
    """删除单条视频记录（一个画质）；delete_file 时同时删除本地文件。

    本地导入的视频文件已复制进 data/local_videos/，删除时一并删掉。
    """
    with get_conn() as c:
        row = c.execute("SELECT local_path FROM videos WHERE id=?", (video_id,)).fetchone()
        c.execute("DELETE FROM videos WHERE id=?", (video_id,))
        c.execute("DELETE FROM danmaku WHERE video_id=?", (video_id,))
        c.execute("DELETE FROM comments WHERE video_id=?", (video_id,))
    if delete_file and row and row["local_path"]:
        _unlink_file(row["local_path"])
    return {"ok": True}


def delete_video_by_bvid(bvid: str, delete_file: bool = True, page: int = -1) -> dict:
    """删除视频（该 BV 的某分P，page<0 则删该 BV 所有分P/画质记录）+ 文件。"""
    if page >= 0:
        rows = [r for r in _rows_videos_by(bvid, page)]
    else:
        rows = _rows_videos_by(bvid)
    with get_conn() as c:
        for r in rows:
            vid = r["id"]
            c.execute("DELETE FROM videos WHERE id=?", (vid,))
            c.execute("DELETE FROM danmaku WHERE video_id=?", (vid,))
            c.execute("DELETE FROM comments WHERE video_id=?", (vid,))
    if delete_file:
        for r in rows:
            if r["local_path"]:
                _unlink_file(r["local_path"])
    return {"ok": True, "deleted": len(rows)}


def _rows_videos_by(bvid: str, page: int = -1) -> list:
    """按 BV（可限定分P page）查视频记录行。"""
    with get_conn() as c:
        if page >= 0:
            return c.execute("SELECT id, local_path FROM videos WHERE bvid=? AND page=?",
                             (bvid, page)).fetchall()
        return c.execute("SELECT id, local_path FROM videos WHERE bvid=?", (bvid,)).fetchall()


def _unlink_file(path: str) -> None:
    try:
        p = Path(path)
        if p.exists():
            p.unlink()
    except Exception:
        pass


# ============ 视频库 ============

# 评论抓取进度（内存态）：video_id -> {fetched, running, error}
_comment_fetch_state: dict[int, dict] = {}
_comment_fetch_lock = threading.Lock()


def get_comment_fetch_progress(video_id: int) -> dict:
    """评论后台抓取进度（供前端轮询显示"抓到哪里了"）。"""
    with _comment_fetch_lock:
        st = _comment_fetch_state.get(video_id)
    if not st:
        return {"running": False, "fetched": 0}
    return dict(st)


def _get_comment_progress(video_id: int) -> int:
    """读该视频上次抓到的最大评论时间戳（增量抓取用）。"""
    with get_conn() as c:
        row = c.execute(
            "SELECT last_ts FROM comment_progress WHERE video_id=?", (video_id,)
        ).fetchone()
    return int(row["last_ts"] or 0) if row else 0


def _save_comment_progress(video_id: int, last_ts: int) -> None:
    """记录该视频评论抓取到的最大时间戳。"""
    with get_conn() as c:
        c.execute(
            "INSERT INTO comment_progress (video_id, last_ts, updated_at) "
            "VALUES (?, ?, datetime('now','localtime')) "
            "ON CONFLICT(video_id) DO UPDATE SET last_ts=excluded.last_ts, "
            "updated_at=datetime('now','localtime')",
            (video_id, last_ts),
        )


def _start_comment_fetch(video_id: int, bvid: str) -> None:
    """后台线程抓评论：每页立即入库 + 更新进度（边抓边显示）。

    增量：记录上次抓到的最大评论时间戳，下次只抓更新的（sort=1 时间排序，
    遇到更早即截断），首次无进度则全量抓。
    """
    state = {"running": True, "fetched": 0, "error": ""}
    with _comment_fetch_lock:
        _comment_fetch_state[video_id] = state

    since_ts = _get_comment_progress(video_id)
    max_ts = since_ts

    def on_page(page_elems):
        nonlocal max_ts
        with get_conn() as c:
            for cm in page_elems:
                ts = cm.get("created_at") or 0
                if ts > max_ts:
                    max_ts = ts
                if cm.get("rpid"):
                    exist = c.execute(
                        "SELECT 1 FROM comments WHERE video_id=? AND rpid=?",
                        (video_id, cm["rpid"]),
                    ).fetchone()
                    if exist:
                        c.execute(
                            "UPDATE comments SET reply_count=? WHERE video_id=? AND rpid=?",
                            (cm.get("reply_count", 0), video_id, cm["rpid"]),
                        )
                    else:
                        c.execute(
                            "INSERT INTO comments "
                            "(video_id, rpid, user_name, content, like_count, created_at, reply_count) "
                            "VALUES (?, ?, ?, ?, ?, ?, ?)",
                            (video_id, cm["rpid"], cm["user_name"], cm["content"],
                             cm["like_count"], str(cm["created_at"]), cm.get("reply_count", 0)),
                        )
        state["fetched"] += len(page_elems)

    try:
        comments = bilibili.fetch_all_comments(bvid, max_pages=500, on_page=on_page,
                                               since_ts=since_ts)
        # 顶层抓完后抓楼中楼（按 reply_count 翻页补齐，50 页安全上限）
        sub_fetched = 0
        with get_conn() as c:
            for cm in comments:
                if not (cm.get("rpid") and cm.get("reply_count", 0) > 0):
                    continue
                got = c.execute(
                    "SELECT COUNT(*) FROM comments WHERE video_id=? AND root=?",
                    (video_id, cm["rpid"]),
                ).fetchone()[0]
                if got >= cm["reply_count"]:
                    continue
                repeat_pages = 0
                for pn in range(1, 51):
                    if cm["reply_count"] <= got:
                        break
                    try:
                        replies = bilibili.fetch_comment_replies(bvid, cm["rpid"], page=pn, page_size=20)
                    except Exception:
                        break
                    if not replies:
                        break
                    page_new = 0
                    for rp in replies:
                        cur = c.execute(
                            "INSERT OR IGNORE INTO comments "
                            "(video_id, rpid, user_name, content, like_count, created_at, reply_to, root) "
                            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                            (video_id, rp.get("rpid"), rp["user_name"], rp["content"],
                             rp["like_count"], str(rp["created_at"]), cm["rpid"], cm["rpid"]),
                        )
                        if cur.rowcount:
                            got += 1
                            page_new += 1
                            sub_fetched += 1
                    # 深翻页返回重复数据：连续 3 页无新增即停
                    if page_new == 0:
                        repeat_pages += 1
                        if repeat_pages >= 3:
                            break
                    else:
                        repeat_pages = 0
        state["fetched"] += sub_fetched
        # 记录增量进度：抓到的最大评论时间戳（下次只抓更新的）
        if max_ts > since_ts:
            _save_comment_progress(video_id, max_ts)
    except Exception as e:
        state["error"] = str(e)
        logger.warning("评论抓取失败 video=%s: %s", video_id, e)
    finally:
        state["running"] = False


_QUALITY_STEPS = [144, 240, 360, 480, 720, 1080, 1440, 2160]


def _quality_from_height(h: int) -> str:
    """把像素高度归到标准清晰度标签（就近取）。"""
    if h <= 0:
        return ""
    best = min(_QUALITY_STEPS, key=lambda x: abs(x - h))
    return "{}P".format(best)


def _backfill_local_quality() -> None:
    """老的非 B 站视频（bvid=''）quality 为空时，用 ffprobe 读高度回填「360P」这类标签。

    ★ 用户反馈：YouTube 视频在首页显示"未知画质" —— 因为早期通用下载入库时没写 quality。
      新下载的已经会写；这里把历史记录补上。
    """
    try:
        import json as _json
        from . import ffmpeg_tools as _ff
        probe = _ff.find_ffprobe(None, required=False)
        if not probe:
            return
        with get_conn() as c:
            rows = c.execute(
                "SELECT id, local_path FROM videos WHERE (bvid IS NULL OR bvid='') "
                "AND (quality IS NULL OR quality='') LIMIT 200"
            ).fetchall()
        for r in rows:
            p = str(r["local_path"] or "")
            if not p or not Path(p).exists():
                continue
            try:
                rr = subprocess.run(
                    [probe, "-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=height", "-of", "json", p],
                    capture_output=True, text=True, timeout=30, encoding="utf-8", errors="replace",
                )
                data = _json.loads(rr.stdout or "{}")
                streams = data.get("streams") or []
                h = int(streams[0].get("height") or 0) if streams else 0
                q = _quality_from_height(h)
                if q:
                    with get_conn() as c2:
                        c2.execute("UPDATE videos SET quality=? WHERE id=?", (q, r["id"]))
                    logger.info("画质回填: video_id=%s → %s（height=%s）", r["id"], q, h)
            except Exception:
                continue
    except Exception as e:
        logger.warning("画质回填失败: %s", e)

def list_videos() -> dict:
    """视频列表：按 (BV, 分P) 分组（同一视频多画质合并为一条，多P各为独立视频），主条带 versions 供播放器切画质。"""
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, bvid, title, up_name, desc, local_path, quality, page, duration, created_at, cover_path, pub_time, subtitle_path "
            "FROM videos ORDER BY id DESC"
        ).fetchall()
    groups: dict[str, list] = {}
    for r in rows:
        r = dict(r)
        # 分组键 = bvid + 分P序号（同一 BV 的不同分P是独立视频；同分P多画质合并）
        key = "{}-{}".format(r["bvid"] or "local{}".format(r["id"]), r.get("page") or 0)
        groups.setdefault(key, []).append(r)

    # ★ 手动排序：读取 video_order 表（首页拖拽后保存的顺序；没有记录的保持原顺序排在后面）
    _order: dict[str, int] = {}
    try:
        with get_conn() as c:
            for r in c.execute("SELECT key, pos FROM video_order").fetchall():
                _order[r["key"]] = int(r["pos"] or 0)
    except Exception:
        pass

    out = []
    for key, items in groups.items():
        items.sort(key=lambda v: _QUALITY_RANK.get(v.get("quality", ""), 0), reverse=True)
        main = dict(items[0])  # 用最高画质作主条
        # 来源标记：有 bvid = B站视频，否则 = 本地导入
        main["source"] = "bilibili" if main.get("bvid") else "local"
        main["order_key"] = key   # ★ 首页手动排序用的键（与拖拽保存的一致）
        main["page"] = main.get("page") or 0
        main["duration"] = _video_duration_ms(main)   # 毫秒；缺则用 ffmpeg 补算并缓存
        main["versions"] = [
            {"id": v["id"], "quality": v["quality"], "local_path": v["local_path"]}
            for v in items
        ]
        # 封面：文件不存在就当没有（前端据此显示占位色块）；顺手在后台给老记录补封面
        cp = main.get("cover_path") or ""
        main["cover_path"] = cp if (cp and Path(cp).exists()) else ""
        out.append(main)
    # ★ 按手动顺序排列：有记录的在前面按 pos；没记录的保持原有顺序排在后面
    if _order:
        _idx = {id(v): i for i, v in enumerate(out)}
        out.sort(key=lambda v: (_order.get(v.get("order_key") or "", 10 ** 9), _idx[id(v)]))
    if any(not (v.get("quality") or "") for v in out if not v.get("bvid")):
        threading.Thread(target=_backfill_local_quality, daemon=True).start()   # 老 YouTube/本地视频补画质标签
    if any(not v.get("cover_path") for v in out):
        threading.Thread(target=_backfill_covers, daemon=True).start()   # 内部有节流，立即返回
    return {"videos": out}


def set_video_order(keys: list) -> dict:
    """保存首页卡片的手动顺序（拖拽后调用）。keys = order_key 列表，按新顺序。"""
    clean = [str(k) for k in (keys or []) if str(k)]
    with get_conn() as c:
        for i, k in enumerate(clean):
            c.execute("INSERT INTO video_order (key, pos) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET pos=excluded.pos", (k, i))
    return {"ok": True, "count": len(clean)}


def set_playlist_order(playlist_id: int, video_ids: list) -> dict:
    """合集内拖拽排序：按传入顺序重排 playlist_items.position（= 显示时的 P 几）。"""
    ids = []
    for x in video_ids or []:
        try:
            ids.append(int(x))
        except Exception:
            pass
    with get_conn() as c:
        for i, vid in enumerate(ids):
            c.execute(
                "UPDATE playlist_items SET position=? WHERE playlist_id=? AND media_id=?",
                (i + 1, playlist_id, vid),
            )
    return {"ok": True, "count": len(ids)}

def _video_duration_ms(v: dict) -> int:
    """视频时长（毫秒）：表里有则返回；否则用 ffmpeg -i 从本地文件头读（fast，不解码），并回填缓存。"""
    d = int(v.get("duration") or 0)
    if d > 0:
        return d
    path = v.get("local_path") or ""
    if not path or not Path(path).exists():
        return 0
    import re, subprocess
    ff = _find_ffmpeg()
    try:
        r = subprocess.run([ff, "-hide_banner", "-i", str(path)], capture_output=True, text=True, timeout=20)
        m = re.search(r"Duration:\s*(\d+):(\d+):(\d+\.?\d*)", r.stderr)
        if m:
            h, mn, s = int(m.group(1)), int(m.group(2)), float(m.group(3))
            d = int((h * 3600 + mn * 60 + s) * 1000)
            with get_conn() as c:
                c.execute("UPDATE videos SET duration=? WHERE id=?", (d, v["id"]))
            return d
    except Exception:
        pass
    return 0


def import_local_video(path: str, title: str = "") -> dict:
    """导入本地视频文件：复制到媒体库 data/local_videos/，登记进 videos 表。

    - 文件复制到库目录（独立管理，删除视频时文件一并删）；
    - bvid 为空 = 本地来源（前端以此区分 B站视频）。
    """
    if not path:
        raise ValueError("文件路径为空")
    src = Path(path)
    if not src.exists():
        raise ValueError("文件不存在：{}".format(path))
    if not src.is_file():
        raise ValueError("不是文件：{}".format(path))

    # 目标目录：data/local_videos/
    local_dir = DATA_DIR / "local_videos"
    local_dir.mkdir(parents=True, exist_ok=True)
    # 目标文件名：防重名加序号（如 视频名 (1).mp4）
    name = title.strip() or src.stem
    dst = local_dir / (src.name)
    n = 1
    while dst.exists():
        dst = local_dir / "{} ({}).{}".format(src.stem, n, src.suffix.lstrip("."))
        n += 1

    with get_conn() as c:
        # 同目标路径去重（同文件重复导入）
        row = c.execute("SELECT id FROM videos WHERE local_path=?", (str(dst),)).fetchone()
        if row:
            return {"ok": True, "video_id": int(row["id"]), "duplicate": True}

    # 复制文件（可中断：先复制到临时名再改名）
    import shutil
    tmp_dst = dst.with_suffix(dst.suffix + ".part")
    shutil.copy2(str(src), str(tmp_dst))
    tmp_dst.rename(dst)

    # ★ 本地视频：发布日期 = 源文件创建时间（Windows 上 st_ctime 就是创建时间）
    try:
        pub_time = int(src.stat().st_ctime)
    except Exception:
        pub_time = 0
    with get_conn() as c:
        cur = c.execute(
            "INSERT INTO videos (bvid, title, up_name, local_path, quality, pub_time, desc) "
            "VALUES ('', ?, '本地视频', ?, '', ?, '')",
            (name, str(dst), pub_time),
        )
        vid = int(cur.lastrowid)
    return {"ok": True, "video_id": vid, "duplicate": False, "copied_to": str(dst),
            "pub_time": pub_time}


def update_video_desc(video_id: int, desc: str) -> dict:
    """更新视频简介（本地添加的视频允许编辑）。"""
    with get_conn() as c:
        row = c.execute("SELECT id FROM videos WHERE id=?", (video_id,)).fetchone()
        if not row:
            return {"ok": False, "error": "视频不存在"}
        c.execute("UPDATE videos SET desc=? WHERE id=?", (desc or "", video_id))
    return {"ok": True, "video_id": video_id, "desc": desc or ""}


def get_video_record(bvid: str, page: int = 0, quality: Optional[str] = None) -> Optional[dict]:
    """按 BV 号 + 分P（可选画质）查视频记录。

    - page < 0：任意分P；quality 为空：任意画质（返回画质最高的那条）。
    ★ 2026-10-14：新增 quality 参数。以前只按 (bvid, page) 判断"是否已下载"，
      于是"下过 720P"就被当成"这个分P 下过了"，同分P 换个画质会被误判为已下载。
    """
    if not bvid:
        return None
    rows = list_video_records(bvid, page, quality)
    if not rows:
        return None
    r = rows[0]
    r["has_file"] = bool(r.get("local_path")) and Path(r["local_path"]).exists()
    r["file_size"] = Path(r["local_path"]).stat().st_size if r["has_file"] else 0
    return r


def list_video_records(bvid: str, page: int = 0, quality: Optional[str] = None) -> list:
    """列出该 (BV, 分P) 已下载的所有画质记录，按画质从高到低排序。

    供界面标注"这个画质已经有本地文件了"、以及下载前判断"该画质是否已下过"。
    """
    if not bvid:
        return []
    with get_conn() as c:
        sql = ("SELECT id, title, local_path, created_at, quality, page FROM videos "
               "WHERE bvid=?")
        args: list = [bvid]
        if page is not None and page >= 0:
            sql += " AND page=?"
            args.append(page)
        if quality:
            sql += " AND quality=?"
            args.append(quality)
        rows = [dict(r) for r in c.execute(sql, args).fetchall()]
    rows.sort(key=lambda v: _QUALITY_RANK.get(v.get("quality", ""), 0), reverse=True)
    for r in rows:
        p = r.get("local_path") or ""
        r["has_file"] = bool(p) and Path(p).exists()
        r["file_size"] = Path(p).stat().st_size if r["has_file"] else 0
    return rows


def video_danmaku(vid: int) -> dict:
    with get_conn() as c:
        rows = c.execute(
            "SELECT time, mode, size, color, text FROM danmaku WHERE video_id=? ORDER BY time",
            (vid,),
        ).fetchall()
    return {"danmaku": [dict(r) for r in rows], "count": len(rows)}


def video_comments(vid: int, page: int = 1) -> dict:
    """已入库直接返回（顶层评论分页 + 预览子评论）；否则实时抓取。"""
    page = max(1, page)
    page_size = 20
    with get_conn() as c:
        row = c.execute("SELECT bvid FROM videos WHERE id=?", (vid,)).fetchone()
        rows = c.execute(
            "SELECT rpid, user_name, content, like_count, created_at, reply_count FROM comments "
            "WHERE video_id=? AND reply_to IS NULL "
            "ORDER BY like_count DESC LIMIT ? OFFSET ?",
            (vid, page_size, (page - 1) * page_size),
        ).fetchall()
    if rows:
        # 每条顶层评论附预览子评论（最多 2 条，按时间正序；嵌套回复也归到本楼）
        out = []
        for top in rows:
            item = dict(top)
            subs = c.execute(
                "SELECT user_name, content, like_count, created_at FROM comments "
                "WHERE video_id=? AND root=? ORDER BY created_at ASC LIMIT 2",
                (vid, top["rpid"]),
            ).fetchall()
            item["replies"] = [dict(s) for s in subs]
            out.append(item)
        # 总数 = 顶层 + 全部子评论（评论页括号计数用）
        total = c.execute(
            "SELECT COUNT(*) FROM comments WHERE video_id=?", (vid,)
        ).fetchone()[0]
        top_total = c.execute(
            "SELECT COUNT(*) FROM comments WHERE video_id=? AND reply_to IS NULL", (vid,)
        ).fetchone()[0]
        return {"comments": out, "total": total, "top_total": top_total,
                "page": page, "page_size": page_size, "cached": True}

    if not row or not row["bvid"]:
        raise ValueError("视频不存在或无 BV 号")
    comments = bilibili.fetch_comments(row["bvid"], page=page)
    with get_conn() as c:
        # ★ 主接口自带的楼中楼预览也存下来（以前丢掉 → 界面里几乎没有回复可显示）
        for cm in comments:
            if cm.get("replies"):
                _store_replies(c, vid, cm.get("rpid"), cm["replies"])
        for cm in comments:
            c.execute(
                "INSERT OR IGNORE INTO comments (video_id, rpid, user_name, content, like_count, created_at, reply_count) "
                "VALUES (?, ?, ?, ?, ?, ?, ?)",
                (vid, cm.get("rpid"), cm["user_name"], cm["content"],
                 cm["like_count"], str(cm["created_at"]), cm.get("reply_count", 0)),
            )
    return {"comments": comments, "cached": False}


def comment_detail(vid: int, rpid: int) -> dict:
    """楼中楼详情：楼主评论 + 全部子评论（按时间正序）。

    若库里子评论不足（可能只抓了第 1 页），实时向 B 站补齐后返回。
    """
    with get_conn() as c:
        row = c.execute("SELECT bvid FROM videos WHERE id=?", (vid,)).fetchone()
        root = c.execute(
            "SELECT rpid, user_name, content, like_count, created_at, reply_count FROM comments "
            "WHERE video_id=? AND rpid=?", (vid, rpid),
        ).fetchone()
        subs = c.execute(
            "SELECT user_name, content, like_count, created_at FROM comments "
            "WHERE video_id=? AND root=? ORDER BY created_at ASC",
            (vid, rpid),
        ).fetchall()
    if not row or not row["bvid"] or not root:
        raise ValueError("评论不存在")
    root_item = dict(root)
    sub_list = [dict(s) for s in subs]

    # 子评论不足时实时补齐（以 B 站 rcount 为准，翻页直到拿满或接口空/连续重复；
    # 50 页（1000 条）安全上限防极端虚标导致的无限请求）
    want = root_item.get("reply_count") or 0
    if want > len(sub_list) and row["bvid"]:
        fetched: list[dict] = []
        fetched_rpids: set[int] = set()
        repeat_pages = 0
        for pn in range(1, 51):
            if want <= len(sub_list) + len(fetched):
                break
            try:
                reps = bilibili.fetch_comment_replies(row["bvid"], rpid, page=pn, page_size=20)
            except Exception:
                break
            if not reps:
                break
            new_count = 0
            for rp in reps:
                rp_id = rp.get("rpid")
                if rp_id and rp_id in fetched_rpids:
                    continue
                if rp_id:
                    fetched_rpids.add(rp_id)
                fetched.append(rp)
                new_count += 1
            # B站楼中楼接口深翻页会返回重复数据：连续 3 页无新增即视为抓完
            if new_count == 0:
                repeat_pages += 1
                if repeat_pages >= 3:
                    break
            else:
                repeat_pages = 0
        if fetched:
            with get_conn() as c:
                for rp in fetched:
                    c.execute(
                        "INSERT OR IGNORE INTO comments "
                        "(video_id, rpid, user_name, content, like_count, created_at, reply_to, root) "
                        "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                        (vid, rp.get("rpid"), rp["user_name"], rp["content"],
                         rp["like_count"], str(rp["created_at"]), rp.get("parent") or rpid, rpid),
                    )
            with get_conn() as c:
                sub_list = [
                    dict(s) for s in c.execute(
                        "SELECT user_name, content, like_count, created_at FROM comments "
                        "WHERE video_id=? AND root=? ORDER BY created_at ASC",
                        (vid, rpid),
                    ).fetchall()
                ]

    return {"root": root_item, "replies": sub_list}


def _get_danmaku_progress(video_id: int) -> tuple[int, set[str], list[str]]:
    """读该视频弹幕抓取进度：返回 (cid, 已抓日期集合, 已知日期列表)。"""
    with get_conn() as c:
        row = c.execute(
            "SELECT cid, fetched_dates FROM danmaku_progress WHERE video_id=?",
            (video_id,),
        ).fetchone()
    if not row:
        return 0, set(), []
    try:
        payload = json.loads(row["fetched_dates"] or "{}")
    except Exception:
        payload = {}
    fetched = set(payload.get("fetched", []) if isinstance(payload, dict) else payload)
    known = payload.get("dates", []) if isinstance(payload, dict) else []
    return int(row["cid"] or 0), fetched, known


def _save_danmaku_progress(video_id: int, cid: int, fetched: set[str],
                           known_dates: list[str] | None = None,
                           conn=None) -> None:
    """记录该视频已抓取的历史弹幕日期（每天抓完立即存，断点续传）。

    known_dates：缓存 B站日期列表（日期接口限流，避免重复现查）。
    conn：可选，复用调用方的连接（避免嵌套连接导致 SQLite 写锁）。
    """
    payload = {"fetched": sorted(fetched), "dates": known_dates or []}
    if conn is not None:
        conn.execute(
            "INSERT INTO danmaku_progress (video_id, cid, fetched_dates, updated_at) "
            "VALUES (?, ?, ?, datetime('now','localtime')) "
            "ON CONFLICT(video_id) DO UPDATE SET "
            "cid=excluded.cid, fetched_dates=excluded.fetched_dates, "
            "updated_at=datetime('now','localtime')",
            (video_id, cid, json.dumps(payload)),
        )
        return
    with get_conn() as c:
        c.execute(
            "INSERT INTO danmaku_progress (video_id, cid, fetched_dates, updated_at) "
            "VALUES (?, ?, ?, datetime('now','localtime')) "
            "ON CONFLICT(video_id) DO UPDATE SET "
            "cid=excluded.cid, fetched_dates=excluded.fetched_dates, "
            "updated_at=datetime('now','localtime')",
            (video_id, cid, json.dumps(payload)),
        )


def refresh_video(video_id: int) -> dict:
    """增量更新弹幕与评论：全量重抓 B 站，按唯一 id 去重入库（只新增缺失的）。

    旧版无 id 数据会被清理后由全量抓取补齐（带 dm_id/rpid，之后增量生效）。
    """
    with get_conn() as c:
        row = c.execute("SELECT bvid FROM videos WHERE id=?", (video_id,)).fetchone()
    if not row or not row["bvid"]:
        raise ValueError("视频不存在或无 BV 号")
    bvid = row["bvid"]

    # ---- 弹幕（历史接口全量 + 实时补缺，断点续传：每天抓完立即入库+记进度）----
    bilibili.set_login_cookie(get_cookie())
    info = bilibili.get_video_info(bvid)
    cid = info["cid"]
    _, fetched, known = _get_danmaku_progress(video_id)
    added_dm = 0
    day_count = 0
    with get_conn() as c:
        # 清理旧版无 id 的记录（避免与带 id 的新数据重复）
        c.execute("DELETE FROM danmaku WHERE video_id=? AND dm_id IS NULL", (video_id,))
        for date, elems in bilibili.fetch_all_danmaku_incremental(cid, fetched, known):
            for d in elems:
                cur = c.execute(
                    "INSERT OR IGNORE INTO danmaku (video_id, dm_id, time, mode, size, color, text) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?)",
                    (video_id, d.get("id"), d["time"], d["mode"], d["size"], d["color"], d["text"]),
                )
                added_dm += cur.rowcount
            if date:
                fetched.add(date)
                day_count += 1
                if date not in known:
                    known.append(date)   # 新日期并入缓存（下次增量能识别）
                # 每天抓完立即记进度（复用外层连接，避免嵌套写锁）
                _save_danmaku_progress(video_id, cid, fetched, known, conn=c)
        total_dm = c.execute(
            "SELECT COUNT(*) FROM danmaku WHERE video_id=?", (video_id,)
        ).fetchone()[0]
    # 若还没缓存日期列表且本次有抓到历史，把 B站日期列表补进进度（限流下不再现查）
    if not known and day_count:
        try:
            dl = bilibili.fetch_history_danmaku_dates(cid)
            if dl:
                _save_danmaku_progress(video_id, cid, fetched, dl)
        except Exception:
            pass
    logger.info("弹幕刷新 video=%s 新增=%s 历史天数=%s 总数=%s",
                video_id, added_dm, day_count, total_dm)

    # ---- 评论（后台线程抓取：每页立即入库，前端轮询进度边抓边显示）----
    bilibili.set_login_cookie(get_cookie())
    threading.Thread(
        target=_start_comment_fetch,
        args=(video_id, bvid),
        daemon=True,
    ).start()

    return {
        "danmaku_added": added_dm,
        "danmaku_total": total_dm,
        "comments_fetching": True,
    }


# ============ 增强任务 ============

def _node_key(node) -> str:
    """节点统一形状：兼容老的字符串节点和新的 {key, params} 字典节点。"""
    if isinstance(node, dict):
        return str(node.get("key") or "").strip()
    return str(node or "").strip()


def _node_params(node) -> dict:
    """节点的参数（老字符串节点 = 空参数）。"""
    if isinstance(node, dict) and isinstance(node.get("params"), dict):
        return dict(node["params"])
    return {}


def _norm_nodes(nodes) -> list:
    """各种形态的节点序列 → 统一 [{"key":..., "params":{...}}]（丢掉空节点）。"""
    out = []
    for n in nodes or []:
        k = _node_key(n)
        if not k:
            continue
        out.append({"key": k, "params": _node_params(n)})
    return out


def _task_enhancers(task: dict) -> list:
    """从 task 行取**节点序列**：优先 enhancers_json，回退老的 enhancer 单列。

    ★ T27 决策：一条链 = 一条 enhance_tasks 记录 + 节点序列；老记录只有 enhancer 单列，
      靠这里兼容（读到空 json 就当成单节点链）。
    ★ 2026-10-20：节点可带参数 → 统一返回 [{"key":..., "params":{...}}]，老字符串数组自动兼容。
    """
    raw = ""
    try:
        raw = task.get("enhancers_json") or ""
    except Exception:
        raw = ""
    if raw:
        try:
            arr = json.loads(raw)
            if isinstance(arr, list):
                nodes = _norm_nodes(arr)
                if nodes:
                    return nodes
        except Exception:
            logger.warning("enhancers_json 解析失败: %r", raw)
    one = (task.get("enhancer") or "").strip()
    return [{"key": one, "params": {}}] if one else []


def _chain_name(nodes: list) -> str:
    """节点序列 → 可读链名（例："Hi-Res重采样→响度统一"）。兼容两种节点形状。"""
    keys = [_node_key(n) for n in nodes]
    return "→".join(_ENHANCER_NAMES.get(k, k) for k in keys if k)


def _fmt_param_value(spec: dict, v) -> str:
    """参数值 → 人话（节点图/血统里显示用）。"""
    typ = spec.get("type") or "choice"
    unit = spec.get("unit") or ""
    if typ == "bool":
        return "开" if v else "关"
    if typ in ("int", "float"):
        try:
            if typ == "int":
                return "{}{}".format(int(round(float(v))), (" " + unit) if unit else "")
            f = float(v)
            txt = ("{}".format(int(f)) if abs(f - round(f)) < 0.0001 else "{:g}".format(f))
            return txt + ((" " + unit) if unit else "")
        except (TypeError, ValueError):
            return str(v)
    if v is None:
        return "无"
    if isinstance(v, bool):
        return "开" if v else "关"
    if isinstance(v, (int, float)) and unit:
        if isinstance(v, int) or abs(float(v) - round(float(v))) < 0.0001:
            return "{} {}".format(int(round(float(v))), unit)
        return "{:g} {}".format(float(v), unit)
    return str(v)


def _node_display(nodes: list) -> list:
    """节点序列 → 可直接显示的明细（含参数文字），供血统/产物列表展示。

    返回 [{"key","label","params","params_text","text"}]；
    text = "高精度重采样（目标采样率 48000 Hz · 品质 HQ · 位深 24 bit）"
    """
    out = []
    for n in nodes or []:
        key = _node_key(n)
        if not key:
            continue
        params = _node_params(n)
        specs = {s["key"]: s for s in ai_pipeline.enhancer_params(key)}
        parts = []
        for k, v in params.items():
            spec = specs.get(k)
            if spec is not None:
                name = spec.get("label") or k
                parts.append("{} {}".format(name, _fmt_param_value(spec, v)))
            else:
                parts.append("{}={}".format(k, v))
        label = _ENHANCER_NAMES.get(key, key)
        ptext = " · ".join(parts)
        out.append({
            "key": key,
            "label": label,
            "params": params,
            "params_text": ptext,
            "text": label + ("（{}）".format(ptext) if ptext else ""),
        })
    return out


def _chain_name_detail(nodes: list) -> str:
    """带参数的链名（例："高精度重采样（目标采样率 48000 Hz · 品质 HQ）→响度统一"）。"""
    return " → ".join(d["text"] for d in _node_display(nodes))


def _probe_audio_stream(src: Path):
    """探测第一个音频流的 (采样率, 声道数)；失败返回 (0, 0)。

    ★ T27：以前 `_enhance_video_audio` 抽音轨**写死 -ar 48000** —— 44.1kHz 源会被无谓重采样，
      96kHz 增强版再增强时第一步就被砍回 48k。这里探测真实采样率供抽音轨保持。
    """
    fp = ffmpeg_tools.find_ffprobe(required=False)
    if not fp:
        return 0, 0
    try:
        r = subprocess.run(
            [fp, "-v", "error", "-select_streams", "a:0",
             "-show_entries", "stream=sample_rate,channels,codec_name",
             "-of", "json", str(src)],
            capture_output=True, text=True, timeout=60,
        )
        st = (json.loads(r.stdout or "{}") or {}).get("streams") or []
        if not st:
            return 0, 0
        return int(st[0].get("sample_rate") or 0), int(st[0].get("channels") or 0)
    except Exception as e:
        logger.warning("探测音频流失败 %s: %s", src, e)
        return 0, 0


_VIDEO_SUFFIXES = {".mp4", ".mkv", ".mov", ".webm", ".avi", ".flv", ".ts", ".m4v"}


def _is_video_file(p: Path) -> bool:
    return (Path(p).suffix or "").lower() in _VIDEO_SUFFIXES


def _ffmpeg_tail(stderr: str, n: int = 3) -> str:
    lines = (stderr or "").strip().splitlines()
    return " | ".join(lines[-n:]) if lines else "（无 stderr）"


def _find_parent_task(source_path: str):
    """用 source_path（= 某个已增强产物的 output_path）反查父 task。"""
    if not source_path:
        return None
    with get_conn() as c:
        row = c.execute(
            "SELECT id, media_id, media_type, enhancer, output_path FROM enhance_tasks "
            "WHERE output_path=? ORDER BY id DESC LIMIT 1", (source_path,)
        ).fetchone()
    return dict(row) if row else None


def _inherit_ownership(media_id: int, media_type: str, source_path: str):
    """★ 归属继承（T27）：media_id<=0 或 media_type 空时，按 source_path 反查父 task 继承归属。

    修掉的现存 bug：对"增强版"再次提交增强时前端只能传 source_path（media_id 丢失）→
    task 的 media_id 变成 0 → 输出文件名成了 `media0_xxx`、版本列表里也归不到位。
    返回 (media_id, media_type, parent_id)。
    """
    parent = _find_parent_task(source_path)
    parent_id = int(parent["id"]) if parent else 0
    if ((not media_id or int(media_id) <= 0) or not media_type) and parent:
        old = (media_id, media_type)
        media_id = int(parent.get("media_id") or 0)
        media_type = (parent.get("media_type") or media_type or "video")
        logger.info("归属继承: source_path=%s → 父 task#%s；media %s → %s/%s",
                    source_path, parent_id, old, media_type, media_id)
    return int(media_id or 0), (media_type or "video"), parent_id


def submit_enhance(media_id: int, media_type: str, enhancer: str, device: str = "cuda",
                   source_path: str = "") -> dict:
    """提交增强任务（**兼容旧接口 = 单节点链**）。

    source_path 非空 = 链式增强（对已增强版本再增强）；归属会自动继承父 task。
    """
    res = submit_enhance_chain(media_id, media_type, source_path, [enhancer], device)
    return {"task_id": res["task_id"]}


def submit_enhance_chain(media_id: int, media_type: str, source_path: str,
                         enhancers: list = None, device: str = "cuda",
                         nodes: list = None) -> dict:
    """提交**增强链**（多节点串行）。一条 task 记录 + `enhancers_json` 节点序列。

    nodes / enhancers 两种入参都接受：
      · 老前端：enhancers = ["loudness", "soxr_hires"]
      · 新前端：nodes = [{"key":"soxr_hires","params":{"target_sr":48000}}, ...]
    返回 {"task_id", "enhancers", "nodes", "chain_name"}。
    """
    raw = nodes if nodes else enhancers
    chain_nodes = _norm_nodes(raw)
    if not chain_nodes:
        raise ValueError("增强链至少要有一个节点")
    if len(chain_nodes) > 8:
        raise ValueError("增强链最多 8 个节点（收到 {} 个）".format(len(chain_nodes)))
    available = set(ai_pipeline.list_enhancers())
    unknown = [n["key"] for n in chain_nodes if n["key"] not in available]
    if unknown:
        raise ValueError("未注册的增强器: {}（可用: {}）".format(unknown, sorted(available)))
    # ★ 参数按 schema 校验（非法值直接 400，别等跑到一半才报错）
    for n in chain_nodes:
        n["params"] = ai_pipeline.sanitize_params(n["key"], n.get("params"))

    media_id, media_type, parent_id = _inherit_ownership(media_id, media_type, source_path)
    keys = [n["key"] for n in chain_nodes]
    name = _chain_name(chain_nodes)
    with get_conn() as c:
        cur = c.execute(
            "INSERT INTO enhance_tasks "
            "(media_id, media_type, enhancer, status, source_path, parent_id, enhancers_json) "
            "VALUES (?, ?, ?, 'pending', ?, ?, ?)",
            (media_id, media_type, keys[0], source_path, parent_id,
             json.dumps(chain_nodes, ensure_ascii=False)),
        )
        task_id = int(cur.lastrowid)
    logger.info("增强链已提交 task#%s media=%s/%s 节点=%s 链名=%s 父task=%s",
                task_id, media_type, media_id, chain_nodes, name, parent_id)
    threading.Thread(target=_run_enhance, args=(task_id, device), daemon=True).start()
    return {"task_id": task_id, "enhancers": keys, "nodes": chain_nodes, "chain_name": name}


def _resolve_media_path(media_type: str, media_id: int) -> str:
    with get_conn() as c:
        if media_type == "audio":
            row = c.execute("SELECT file_path FROM songs WHERE id=?", (media_id,)).fetchone()
        else:
            row = c.execute("SELECT local_path FROM videos WHERE id=?", (media_id,)).fetchone()
    if not row or not row[0]:
        raise ValueError(f"{media_type} #{media_id} 不存在或无文件")
    return row[0]


def _media_title(media_type: str, media_id: int) -> str:
    """取媒体标题（用于增强输出文件名的可读命名）。缺标题回退到文件名/ID。"""
    title = ""
    try:
        with get_conn() as c:
            if media_type == "audio":
                row = c.execute("SELECT title, file_path FROM songs WHERE id=?", (media_id,)).fetchone()
            else:
                row = c.execute("SELECT title, local_path FROM videos WHERE id=?", (media_id,)).fetchone()
        if row:
            title = (row["title"] or "").strip()
            if not title and row[1]:
                title = Path(row[1]).stem
    except Exception:
        pass
    return title or "media{}".format(media_id)


EXPORT_DIR = DATA_DIR / "exports"


def export_clip(video_id: int, start_ms: int, end_ms: int,
                reencode: bool = False) -> dict:
    """导出视频片段：ffmpeg 裁剪到 data/exports/。

    - 快速模式（默认）：-c copy 不重编码，秒级完成，起止对齐最近关键帧
    - 精确模式：libx264 + aac 重编码，帧级精确，按片段长度耗时
    """
    src = Path(_resolve_media_path("video", video_id))
    if not src.exists():
        raise RuntimeError("视频文件不存在：{}".format(src))
    if start_ms < 0 or end_ms <= start_ms:
        raise RuntimeError("片段无效：终点必须大于起点")

    with get_conn() as c:
        row = c.execute("SELECT title FROM videos WHERE id=?", (video_id,)).fetchone()
    title = (row["title"] if row and row["title"] else "video{}".format(video_id)).strip()
    safe = re.sub(r'[\\/:*?"<>|]', "_", title)[:60] or "video{}".format(video_id)

    EXPORT_DIR.mkdir(parents=True, exist_ok=True)
    start_s, end_s = start_ms / 1000.0, end_ms / 1000.0
    dst = EXPORT_DIR / "{}_{:.1f}s-{:.1f}s.mp4".format(safe, start_s, end_s)
    n = 1
    while dst.exists():
        dst = EXPORT_DIR / "{}_{:.1f}s-{:.1f}s_{}.mp4".format(safe, start_s, end_s, n)
        n += 1

    ff = _find_ffmpeg()
    if reencode:
        # 精确：输入 seek 快速定位 → 重编码，帧级精确
        cmd = [ff, "-y", "-ss", "{:.3f}".format(start_s), "-to", "{:.3f}".format(end_s),
               "-i", str(src), "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
               "-c:a", "aac", "-b:a", "192k", str(dst)]
    else:
        # 快速：stream copy，不重编码（起止对齐最近关键帧）
        cmd = [ff, "-y", "-ss", "{:.3f}".format(start_s), "-to", "{:.3f}".format(end_s),
               "-i", str(src), "-c", "copy", "-avoid_negative_ts", "make_zero", str(dst)]
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    except subprocess.TimeoutExpired:
        dst.unlink(missing_ok=True)
        raise RuntimeError("导出超时（超过 10 分钟）")
    if proc.returncode != 0:
        dst.unlink(missing_ok=True)
        detail = (proc.stderr or proc.stdout or "").strip()[-500:]
        raise RuntimeError("导出失败：{}".format(detail))
    return {
        "ok": True,
        "video_id": video_id,
        "output_path": str(dst),
        "duration_ms": end_ms - start_ms,
        "size_bytes": dst.stat().st_size,
        "reencoded": reencode,
    }


class _EnhanceCancelled(Exception):
    """增强任务被用户取消。"""


def cancel_enhance_task(task_id: int) -> dict:
    """请求取消增强任务（标记 cancel_requested，任务线程检测后中断）。"""
    with get_conn() as c:
        c.execute(
            "UPDATE enhance_tasks SET cancel_requested=1 "
            "WHERE id=? AND status IN ('pending','running')",
            (task_id,),
        )
    return {"ok": True}


class _NodeFailed(Exception):
    """增强链里第 idx 个节点（name）失败。"""

    def __init__(self, idx: int, name: str, cause: Exception):
        self.idx = idx
        self.name = name
        self.cause = cause
        super().__init__(str(cause))


def _apply_audio_nodes(src: Path, dst: Path, nodes: list, device: str, progress,
                       tmp: Optional[Path] = None, is_video: bool = True) -> Path:
    """把一串**音频节点**串行应用到 src（视频或音频），返回产物路径。

    ★ T27 核心（两处修复）：
      1. 抽音轨**保持源采样率/声道**（以前写死 `-ar 48000`，44.1k 源会多一次重采样、
         96kHz 增强版再增强时第一步就被砍回 48k）；
      2. 节点之间一律用**无损 WAV（pcm_s32le，同采样率/声道）**传递；只有最后视频要回封装时
         才 `-c:a aac` **编码一次** —— 多节点 ≠ 多代 AAC。
    """
    ff = _find_ffmpeg()
    own_tmp = tmp is None
    tmp = Path(tmp) if tmp else Path(tempfile.mkdtemp(prefix="enh_chain_"))
    n = max(1, len(nodes))

    def cb_for(i: int):
        if not progress:
            return None

        def cb(p, processed=None, total=None):
            try:
                pp = float(p or 0)
            except (TypeError, ValueError):
                pp = 0.0
            progress((i + max(0.0, min(1.0, pp))) / n, processed, total)
        return cb

    try:
        # ---- 1) 预备输入 ----
        if is_video:
            rate, ch = _probe_audio_stream(src)
            if rate <= 0:
                rate = 0            # 探测不到：不传 -ar，让 ffmpeg 用源；
                logger.warning("链·探测源采样率失败 → 交给 ffmpeg 自行决定（源 %s）", src.name)
            cur = tmp / "node_in.wav"
            cmd = [ff, "-y", "-v", "error", "-i", str(src), "-vn", "-acodec", "pcm_s32le"]
            if rate > 0:
                cmd += ["-ar", str(rate)]
            if ch > 0:
                cmd += ["-ac", str(ch)]
            cmd += [str(cur)]
            r = subprocess.run(cmd, capture_output=True, text=True)
            if r.returncode != 0 or not cur.exists():
                raise RuntimeError("抽音轨失败：{}".format(_ffmpeg_tail(r.stderr)))
            r_in, c_in = _probe_audio_stream(cur)
            logger.info("链·抽音轨: 源 %dHz/%dch → 中间无损 %s（pcm_s32le %dHz/%dch, %.2f MB）"
                        " ★保持源采样率（旧版写死 48000）",
                        rate or r_in, ch or c_in, cur.name, r_in, c_in, cur.stat().st_size / 1e6)
        else:
            cur = src
            logger.info("链·输入: 音频文件 %s（跳过抽音轨）", Path(src).name)

        # ---- 2) 节点串行（中间全部无损 wav；每个节点可带自己的参数）----
        for i, node in enumerate(nodes):
            node_key = _node_key(node)
            node_params = _node_params(node)
            out = tmp / ("node%d.wav" % (i + 1))
            try:
                ai_pipeline.run_enhance(node_key, cur, out, device=device,
                                        progress_cb=cb_for(i), params=node_params)
            except _EnhanceCancelled:
                raise
            except Exception as e:
                raise _NodeFailed(i + 1, node, e) from e
            if not out.exists():
                raise _NodeFailed(i + 1, node, RuntimeError("增强器没有产出文件"))
            r_out, c_out = _probe_audio_stream(out)
            is_last = (i == len(nodes) - 1)
            if (not is_last) or is_video:
                # 交给下一节点 / 回封装前：统一规范化成 pcm_s32le（同采样率/声道），保证无损交接
                norm = tmp / ("node%d_s32.wav" % (i + 1))
                cmd = [ff, "-y", "-v", "error", "-i", str(out), "-acodec", "pcm_s32le"]
                if r_out > 0:
                    cmd += ["-ar", str(r_out)]
                if c_out > 0:
                    cmd += ["-ac", str(c_out)]
                cmd += [str(norm)]
                r = subprocess.run(cmd, capture_output=True, text=True)
                if r.returncode != 0 or not norm.exists():
                    raise _NodeFailed(i + 1, node, RuntimeError("规范化无损 wav 失败：" + _ffmpeg_tail(r.stderr)))
                logger.info("链·节点 %d/%d（%s）完成 → 无损交接 %s（pcm_s32le %dHz/%dch, %.2f MB）%s",
                            i + 1, len(nodes), _ENHANCER_NAMES.get(node_key, node_key), norm.name,
                            r_out, c_out, norm.stat().st_size / 1e6,
                            "（最后一个节点）" if is_last else "")
                cur = norm
            else:
                logger.info("链·节点 %d/%d（%s）完成 → %s（%dHz/%dch, %.2f MB，音频链最后一节点，保持原格式）",
                            i + 1, len(nodes), _ENHANCER_NAMES.get(node_key, node_key), out.name,
                            r_out, c_out, out.stat().st_size / 1e6)
                cur = out

        # ---- 3) 收尾 ----
        dst = Path(dst)
        dst.parent.mkdir(parents=True, exist_ok=True)
        if is_video:
            r_end, c_end = _probe_audio_stream(cur)
            logger.info("链·★最终编码（整条链只此一次）: -c:v copy + -c:a aac，音轨 %dHz/%dch ← %s",
                        r_end, c_end, Path(cur).name)
            r = subprocess.run(
                [ff, "-y", "-v", "error", "-i", str(src), "-i", str(cur),
                 "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                 "-shortest", str(dst)],
                capture_output=True, text=True,
            )
            if r.returncode != 0 or not dst.exists():
                raise RuntimeError("回封装（只编码一次 AAC）失败：{}".format(_ffmpeg_tail(r.stderr)))
        else:
            shutil.copy2(str(cur), str(dst))
        if progress:
            progress(1.0)
        return dst
    finally:
        if own_tmp:
            shutil.rmtree(tmp, ignore_errors=True)


def _run_chain_nodes(src: Path, dst: Path, nodes: list, device: str, progress) -> Path:
    """按节点序列串行处理（音频节点全走"无损中间 + 最后只编码一次"的最优路径）。

    ★ 节点可以是字符串，也可以是 {"key":..., "params":{...}}（节点图里每个节点单独选参数）。
    """
    if not nodes:
        raise ValueError("增强链没有节点")
    keys = [_node_key(n) for n in nodes]
    tmp = Path(tempfile.mkdtemp(prefix="enh_chain_"))
    try:
        if all(k in AUDIO_ENHANCERS for k in keys):
            # ★ 纯音频节点链：整条链只在最后回封装时编码一次 AAC
            return _apply_audio_nodes(src, dst, nodes, device, progress, tmp=tmp,
                                      is_video=_is_video_file(src))
        # 混合链（含 realesrgan/rife 等视频节点）：逐节点；音频节点作用在视频上时
        # 必须在该节点边界回封装一次（视频节点要的是带音轨的视频文件），日志会写明边界编码。
        n_total = len(nodes)
        cur = src
        for i, node in enumerate(nodes):
            node_key = _node_key(node)
            node_params = _node_params(node)
            last = (i == n_total - 1)
            if node_key in AUDIO_ENHANCERS:
                is_vid = _is_video_file(cur)
                out = dst if last else (tmp / ("mix%d%s" % (i, ".mp4" if is_vid else ".wav")))
                logger.info("链·阶段 %d/%d（%s）: 音频节点作用在 %s 上 参数=%s",
                            i + 1, n_total, node_key, Path(cur).name, node_params)
                _apply_audio_nodes(cur, out, [node], device,
                                   (lambda p, processed=None, total=None, _i=i: progress(
                                       (_i + max(0.0, min(1.0, float(p or 0)))) / n_total, processed, total)) if progress else None,
                                   tmp=tmp, is_video=is_vid)
                cur = out
            else:
                out = dst if last else (tmp / ("v%d.mp4" % i))
                logger.info("链·阶段 %d/%d（%s）: 视频节点，输入 %s → 输出 %s 参数=%s",
                            i + 1, n_total, node_key, Path(cur).name, Path(out).name, node_params)
                try:
                    ai_pipeline.run_enhance(node_key, cur, out, device=device,
                                            progress_cb=(lambda p, processed=None, total=None, _i=i: progress(
                                                (_i + max(0.0, min(1.0, float(p or 0)))) / n_total, processed, total)) if progress else None,
                                            params=node_params)
                except _EnhanceCancelled:
                    raise
                except Exception as e:
                    raise _NodeFailed(i + 1, node_key, e) from e
                cur = out
        return dst
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _run_enhance(task_id: int, device: str = "cuda") -> None:
    """后台跑一条**增强链**：节点串行 → 中间无损 → 最后只编码一次。"""

    def progress(p: float, processed=None, total=None) -> None:
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET progress=?, processed=?, total=? WHERE id=?",
                (round(max(0.0, min(1.0, float(p or 0))) * 100, 1), processed or 0, total or 0, task_id),
            )
            row = c.execute(
                "SELECT cancel_requested FROM enhance_tasks WHERE id=?", (task_id,)
            ).fetchone()
        if row and row["cancel_requested"]:
            raise _EnhanceCancelled()

    try:
        with get_conn() as c:
            row = c.execute("SELECT * FROM enhance_tasks WHERE id=?", (task_id,)).fetchone()
        if not row:
            logger.warning("增强任务 %s 不存在（可能已被删除）", task_id)
            return
        task = dict(row)
        nodes = _task_enhancers(task)
        if not nodes:
            one = (task.get("enhancer") or "").strip()
            nodes = [{"key": one, "params": {}}] if one else []
        nodes = [nd for nd in nodes if _node_key(nd)]
        media_type = task.get("media_type") or "video"
        media_id = int(task.get("media_id") or 0)
        source_path = task.get("source_path") or ""

        if source_path:
            src = Path(source_path)
            if not src.exists():
                raise ValueError("链式增强源文件不存在：{}".format(source_path))
        else:
            src = Path(_resolve_media_path(media_type, media_id))
        with get_conn() as c:
            c.execute("UPDATE enhance_tasks SET status='running' WHERE id=?", (task_id,))

        suffix = src.suffix or ".mp4"
        title = _media_title(media_type, media_id)
        safe_title = re.sub(r'[\\/:*?"<>|]', "_", title).strip()[:60] or "media{}".format(media_id)
        chain = _chain_name(nodes) or task.get("enhancer") or "enhance"
        chain_file = re.sub(r'[\\/:*?"<>|]', "_", chain)
        dst = ENHANCED_DIR / "{}_{}{}".format(safe_title, chain_file, suffix)
        idx = 1
        while dst.exists():   # 重名兜底
            dst = ENHANCED_DIR / "{}_{}{}{}".format(safe_title, chain_file, idx, suffix)
            idx += 1
        logger.info("增强链开跑 task#%s 节点=%s（%d 个）输入=%s 归属=%s/%s",
                    task_id, nodes, len(nodes), src.name, media_type, media_id)
        heavy = any(_node_key(n) in _HEAVY_ENHANCERS for n in nodes)
        if heavy:
            # ★ 安全防护：同一时刻只允许一个重型 GPU 增强在跑（避免显存/内存双份爆掉）
            if not _HEAVY_ENHANCE_LOCK.acquire(blocking=False):
                logger.info("增强任务 #%s 等待其它 GPU 增强任务完成（安全串行，避免显存/内存双份爆掉）…", task_id)
                _HEAVY_ENHANCE_LOCK.acquire()
            try:
                _run_chain_nodes(src, dst, nodes, device, progress)
            finally:
                _HEAVY_ENHANCE_LOCK.release()
        else:
            _run_chain_nodes(src, dst, nodes, device, progress)
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET status='done', progress=100, output_path=? WHERE id=?",
                (str(dst), task_id),
            )
        logger.info("增强链完成 task#%s → %s", task_id, dst)
    except _EnhanceCancelled:
        logger.info("增强任务 %s 被用户取消", task_id)
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET status='cancelled', error='用户取消' WHERE id=?",
                (task_id,),
            )
    except _NodeFailed as e:
        msg = "第 {} 个节点（{}）失败：{}".format(
            e.idx, _ENHANCER_NAMES.get(e.name, e.name), e.cause)
        logger.warning("增强链失败 task=%s: %s", task_id, msg)
        with get_conn() as c:
            c.execute("UPDATE enhance_tasks SET status='failed', error=? WHERE id=?", (msg, task_id))
    except Exception as e:
        logger.warning("增强失败 task=%s: %s", task_id, e)
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET status='failed', error=? WHERE id=?",
                (str(e), task_id),
            )


def _enhance_video_audio(src: Path, dst: Path, enhancer: str, device: str, progress_cb) -> Path:
    """视频音轨增强（**兼容旧签名**）：= 单节点音频链，内部走新的无损路径。

    以前这里抽音轨写死 48k、每步 AAC 编码；现在与增强链共用 `_apply_audio_nodes`。
    """
    return _apply_audio_nodes(Path(src), Path(dst), [{"key": enhancer, "params": {}}], device, progress_cb, is_video=True)


def list_enhance_tasks() -> dict:
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, media_id, media_type, enhancer, enhancers_json, status, progress, processed, total, "
            "output_path, source_path, parent_id, error, created_at "
            "FROM enhance_tasks ORDER BY id DESC LIMIT 50"
        ).fetchall()
    tasks = []
    for r in rows:
        t = dict(r)
        nodes = _task_enhancers(t)
        t["nodes"] = nodes                                  # 新：带参数的节点（节点图/调试用）
        t["enhancers"] = [_node_key(n) for n in nodes]      # 兼容老前端：仍然是 key 字符串数组
        t["node_details"] = _node_display(nodes)            # ★ 带参数的显示明细
        t["chain_name"] = _chain_name(nodes)
        t["chain_name_detail"] = _chain_name_detail(nodes)
        tasks.append(t)
    return {"tasks": tasks}


def _task_dict(task: dict, depth: int = 0) -> dict:
    """task 行 → 契约里 `task`/`ancestors` 项的统一形状。"""
    nodes = _task_enhancers(task)
    out_path = task.get("output_path") or ""
    return {
        "task_id": int(task["id"]),
        "id": int(task["id"]),
        "media_id": int(task.get("media_id") or 0),
        "media_type": task.get("media_type") or "",
        "status": task.get("status") or "",
        "progress": task.get("progress") or 0,
        "error": task.get("error") or "",
        "output_path": out_path,
        "exists": bool(out_path) and Path(out_path).exists(),
        "enhancers": [_node_key(n) for n in nodes],
        "nodes": nodes,
        "node_details": _node_display(nodes),          # ★ 血统显示：带参数的人话明细
        "chain_name": _chain_name(nodes),
        "chain_name_detail": _chain_name_detail(nodes),
        "parent_id": int(task.get("parent_id") or 0),
        "depth": depth,
        "created_at": task.get("created_at") or "",
    }


def get_enhance_chain(task_id: int) -> dict:
    """增强链血缘：本任务 + 逐级祖先 + 链最底输入（原始视频）。

    契约（Lead 定死）：
      {"task": {...}, "ancestors": [从直接父往上...], "source": {kind, media_id, title, path, exists}}
    """
    with get_conn() as c:
        row = c.execute("SELECT * FROM enhance_tasks WHERE id=?", (task_id,)).fetchone()
        if not row:
            raise ValueError("增强任务 #{} 不存在".format(task_id))
        task = dict(row)

        # 逐级往上（防环：最多 32 层 + 见过的不再走）
        ancestors = []
        seen = {int(task["id"])}
        cur = task
        depth = 0
        while True:
            pid = int(cur.get("parent_id") or 0)
            if pid <= 0 or pid in seen or depth >= 32:
                break
            prow = c.execute("SELECT * FROM enhance_tasks WHERE id=?", (pid,)).fetchone()
            if not prow:
                break
            depth += 1
            ptask = dict(prow)
            ancestors.append(_task_dict(ptask, depth))
            seen.add(int(ptask["id"]))
            cur = ptask

        root = cur  # 走到最底：parent_id=0 的那条（或没有父的这条）
        media_id = int(root.get("media_id") or 0)
        media_type = (root.get("media_type") or task.get("media_type") or "video")
        if media_id > 0:
            source = {
                "kind": "media",
                "media_id": media_id,
                "media_type": media_type,
                "title": _media_title(media_type, media_id),
                "path": _safe_resolve_media_path(media_type, media_id),
                "exists": False,
            }
            source["exists"] = bool(source["path"]) and Path(source["path"]).exists()
        else:
            path = root.get("source_path") or ""
            source = {
                "kind": "file",
                "media_id": 0,
                "media_type": media_type,
                "title": Path(path).stem if path else "",
                "path": path,
                "exists": bool(path) and Path(path).exists(),
            }
    return {"task": _task_dict(task, 0), "ancestors": ancestors, "source": source}


def _safe_resolve_media_path(media_type: str, media_id: int) -> str:
    """_resolve_media_path 的"不抛异常"版本（血缘查询用，查不到给空串）。"""
    try:
        return _resolve_media_path(media_type, media_id) if media_id > 0 else ""
    except Exception:
        return ""


# ---- 增强器设备需求（与前端 Heavy 标记一致：需要 GPU/CUDA 的三项）----
_GPU_ENHANCERS = ("audio_sr", "realesrgan", "rife")

# ★ 安全防护：重型 GPU 增强（AI 超分/超分辨率/补帧）串行执行。
#   以前两个任务可以同时跑 → 显存/RAM 双份占用，真实就是这么把电脑挂掉的。
_HEAVY_ENHANCE_LOCK = threading.Lock()
_HEAVY_ENHANCERS = {"audio_sr", "realesrgan", "rife"}


def detect_gpu() -> dict:
    """★ 检测 NVIDIA GPU / CUDA 是否可用（供增强器可用性判断）。
    快（nvidia-smi 约 50ms）、失败不抛：任何异常都返回 available=False + 可读原因。
    ★ 检测失败绝不能影响纯 CPU 的增强器（loudness / soxr_hires / fft_hires / denoise）。
    """
    # 1) nvidia-smi（最快，不需要 torch）
    try:
        r = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=4,
        )
        if r.returncode == 0 and r.stdout.strip():
            line = r.stdout.strip().splitlines()[0].strip()
            return {"available": True, "name": line, "reason": "", "source": "nvidia-smi"}
    except Exception:
        pass
    # 2) 回退 torch（慢，但能确认 CUDA 真的可用）
    try:
        import torch  # type: ignore
        if torch.cuda.is_available():
            name = torch.cuda.get_device_name(0)
            return {"available": True, "name": name, "reason": "", "source": "torch"}
        return {"available": False, "name": "", "reason": "本机没检测到可用的 CUDA 设备（torch 可用但没有 GPU）", "source": "torch"}
    except Exception:
        pass
    return {
        "available": False,
        "name": "",
        "reason": "本机没有检测到可用的 NVIDIA GPU / CUDA（nvidia-smi 与 torch 都不可用）",
        "source": "none",
    }


_DEP_CHECK_CACHE: dict[str, tuple[bool, str]] = {}


def _enhancer_deps_slow(name: str) -> tuple[bool, str]:
    """★ 真实依赖检测（会 import torch 等重包，单次 1~3 秒）——**只能在后台线程跑**。

    为什么：以前 /api/enhancers 直接调它，冷启动第一次要 7 秒多（每个增强器都 subprocess import torch），
    导致刚打开节点图时点按钮像卡住。现在改成：接口先返回缓存值（快），后台线程慢慢刷新。
    """
    if name in ai_deps.FEATURES:
        return ai_deps.feature_ready(name)
    if name in _DEP_CHECK_CACHE:
        return _DEP_CHECK_CACHE[name]
    ok, why = True, ""
    try:
        if name in ("soxr_hires", "fft_hires", "loudness"):
            # 已改成走 ffmpeg，只要求 ffmpeg 可用
            from . import ffmpeg_tools
            ffmpeg_tools.find_ffmpeg(required=True)
        elif name == "denoise":
            import librosa  # noqa: F401
            import soundfile  # noqa: F401
            import torch  # noqa: F401
            from df.enhance import enhance, init_df  # noqa: F401
        elif name == "audio_sr":
            import torch  # noqa: F401
            from LavaSR.model import LavaEnhance2  # noqa: F401
        elif name in ("realesrgan", "rife"):
            import torch  # noqa: F401
        else:
            ok, why = True, ""
    except Exception as e:
        ok = False
        why = ("安装版不含 AI 依赖（{}）。此项需在开发环境的 backend/.venv 里使用；"
               "安装版可用：响度统一 / Hi-Res 重采样 / FFT 重采样").format(
            type(e).__name__ if isinstance(e, ImportError) else e)
    _DEP_CHECK_CACHE[name] = (ok, why)
    return ok, why


# ================= ★ 增强器环境探测：磁盘缓存 + 后台线程（避免冷启动扫描卡 7 秒）=================
_ENV_CACHE_FILE = DATA_DIR / "enhancer_env_cache.json"
_ENV_CACHE: dict = {"loaded": False, "data": {}}
_ENV_PROBE: dict = {"running": False, "last": 0.0}
_ENV_CACHE_TTL = 300.0     # 5 分钟；安装完 AI 依赖后 marker 变化会立即失效


def _load_env_cache() -> dict:
    if _ENV_CACHE["loaded"]:
        return _ENV_CACHE["data"]
    _ENV_CACHE["loaded"] = True
    try:
        _ENV_CACHE["data"] = json.loads(_ENV_CACHE_FILE.read_text(encoding="utf-8"))
    except Exception:
        _ENV_CACHE["data"] = {}
    return _ENV_CACHE["data"]


def _save_env_cache(data: dict) -> None:
    try:
        _ENV_CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = _ENV_CACHE_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        tmp.replace(_ENV_CACHE_FILE)
    except Exception:
        pass


def _marker_stamp() -> float:
    """AI 依赖安装成功标记文件的 mtime（变了 = 依赖变了 → 缓存失效）。"""
    try:
        return float(ai_deps.MARKER.stat().st_mtime)
    except Exception:
        return 0.0


def _env_cache_valid(data: dict) -> bool:
    try:
        ts = float(data.get("ts") or 0)
        if ts <= 0 or (time.time() - ts) > _ENV_CACHE_TTL:
            return False
        if float(data.get("marker") or 0) != _marker_stamp():
            return False
        return bool(data.get("deps"))
    except Exception:
        return False


def _fast_gpu() -> dict:
    """快速 GPU 检测：只跑 nvidia-smi（约 50ms）；不去 import torch（那会慢 2 秒）。"""
    try:
        r = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=3,
        )
        if r.returncode == 0 and r.stdout.strip():
            return {"available": True, "name": r.stdout.strip().splitlines()[0].strip(),
                    "reason": "", "source": "nvidia-smi"}
    except Exception:
        pass
    return {"available": False, "name": "",
            "reason": "本机没检测到可用的 NVIDIA GPU / CUDA（需要 NVIDIA 显卡才能用 GPU 增强）",
            "source": "none"}


def _env_probe_worker() -> None:
    try:
        gpu = detect_gpu()
        deps = {}
        for name in ai_pipeline.list_enhancers():
            try:
                ok, why = _enhancer_deps_slow(name)
            except Exception as e:
                ok, why = True, ""
                logger.warning("环境探测 %s 失败: %s", name, e)
            deps[name] = {"ok": bool(ok), "why": why or ""}
        data = {"ts": time.time(), "marker": _marker_stamp(), "gpu": gpu, "deps": deps}
        _ENV_CACHE["data"] = data
        _ENV_CACHE["loaded"] = True
        _save_env_cache(data)
        logger.info("增强器环境探测完成：GPU=%s 依赖=%s", gpu.get("available"),
                    {k: v["ok"] for k, v in deps.items()})
    finally:
        _ENV_PROBE["running"] = False


def _ensure_env_probe() -> None:
    data = _load_env_cache()
    if _env_cache_valid(data) or _ENV_PROBE["running"]:
        return
    _ENV_PROBE["running"] = True
    threading.Thread(target=_env_probe_worker, daemon=True, name="enhancer-env-probe").start()


def invalidate_enhancer_env_cache() -> None:
    """AI 依赖安装前后调用：让下次 /api/enhancers 重新探测。"""
    try:
        _ENV_CACHE["data"] = {}
        _ENV_CACHE["loaded"] = True
        _ENV_CACHE_FILE.unlink(missing_ok=True)
    except Exception:
        pass


def _enhancer_deps_fast(name: str) -> tuple[bool, str]:
    """快速取依赖状态：有缓存就用缓存；没有先乐观当可用并启动后台探测。"""
    data = _load_env_cache()
    _ensure_env_probe()
    ent = (data.get("deps") or {}).get(name)
    if isinstance(ent, dict):
        return bool(ent.get("ok")), str(ent.get("why") or "")
    return True, ""


def deps_status() -> dict:
    """AI 增强依赖状态（含安装任务进度）。前端「增强任务」页「一键下载 AI 依赖」用。"""
    try:
        return ai_deps.status()
    except Exception as e:
        logger.warning("依赖状态读取失败: %s", e)
        return {"ok": False, "error": str(e)[:300]}


def deps_start_install() -> dict:
    """一键下载/安装 AI 依赖（幂等：已装好 / 正在装都会直接返回，不会重复下载）。"""
    try:
        invalidate_enhancer_env_cache()   # 安装完依赖后要重新探测可用性
        return ai_deps.start_install()
    except Exception as e:
        logger.warning("启动依赖安装失败: %s", e)
        return {"action": "failed", "message": str(e)[:300]}


def deps_cancel_install() -> dict:
    """取消正在进行的 AI 依赖安装（已下载的轮子/缓存保留）。"""
    try:
        return ai_deps.cancel_install()
    except Exception as e:
        return {"action": "failed", "message": str(e)[:300]}

def list_enhancers() -> dict:
    """增强器清单 + 设备可用性（★ 前端据此把不可用的项置灰并显示原因）。

    ★ 冷启动不再扫描 import（以前第一次 7 秒多，导致节点图刚打开时按钮像卡住）：
      · GPU：只跑 nvidia-smi（毫秒级）
      · 依赖：磁盘缓存命中就用；没命中先乐观返回「可用」（checking=True），后台线程慢慢刷新
    """
    names = ai_pipeline.list_enhancers()
    data = _load_env_cache()
    _ensure_env_probe()
    gpu = data.get("gpu") if isinstance(data.get("gpu"), dict) else None
    if not gpu:
        gpu = _fast_gpu()
    checking = not _env_cache_valid(data)
    items = []
    for name in names:
        requires_gpu = name in _GPU_ENHANCERS
        deps_ok, deps_why = _enhancer_deps_fast(name)
        gpu_ok = (not requires_gpu) or bool(gpu.get("available"))
        ok = deps_ok and gpu_ok
        if ok:
            reason = ""
        elif not deps_ok:
            reason = deps_why
        else:
            reason = str(gpu.get("reason") or "需要 GPU")
        items.append({
            "key": name,
            "requires_gpu": requires_gpu,
            "available": ok,
            "reason": reason,
            # ★ 节点图参数：前端按这个 schema 在节点卡上渲染下拉框/开关/滑条
            "params": ai_pipeline.enhancer_params(name),
        })
    return {"enhancers": items, "names": names, "gpu": gpu,
            "checking": checking,   # True = 依赖还在后台检测，前端可稍后再拉一次
            }



def _guess_video_bits(pix_fmt: str, bpr: float) -> tuple:
    """视频位深：优先用码流的 bits_per_raw_sample，拿不到就从像素格式推断。

    yuv420p/p010le/yuv420p10le/yuv444p12le 这类 pix_fmt 本身就编码了位深：
      p010=10bit、p016=16bit、*10le/*12le/*16le/*9le/*14le → 对应位深，其余常规 8bit。
    返回 (位深, 来源说明)；位深 0 表示推断失败。
    """
    if bpr and bpr > 0:
        return int(bpr), "码流"
    pf = (pix_fmt or "").lower()
    if not pf:
        return 0, ""
    if "p016" in pf:
        return 16, "按像素格式推断"
    if "p010" in pf:
        return 10, "按像素格式推断"
    import re as _re
    m = _re.search(r'(\d{1,2})(le|be)$', pf)
    if m:
        bits = int(m.group(1))
        if 8 <= bits <= 16:
            return bits, "按像素格式推断"
    if pf.startswith(("yuv", "nv", "p0", "rgb", "bgr", "gbr", "gray", "ya", "uy", "yu")):
        return 8, "按像素格式推断"
    return 0, ""


def _guess_audio_bits(sample_fmt: str, bpr: float, bps: float) -> tuple:
    """音频位深：优先 bits_per_raw_sample / bits_per_sample（无损格式有真值）；
    拿不到就按 sample_fmt 推断（AAC/MP3 这类有损格式本身没有固定位深，解码后是 32bit 浮点）。
    返回 (位深, 来源说明)；来源里标注"解码格式"表示不是码流真实位深。
    """
    if bpr and bpr > 0:
        return int(bpr), "码流"
    if bps and bps > 0:
        return int(bps), "码流"
    sf = (sample_fmt or "").lower()
    if not sf:
        return 0, ""
    if sf.startswith("u8"):
        return 8, "解码格式"
    if sf.startswith("s16"):
        return 16, "解码格式"
    if sf.startswith("s32"):
        return 32, "解码格式（整数）"
    if sf.startswith("flt"):
        return 32, "解码格式（浮点）"
    if sf.startswith("dbl"):
        return 64, "解码格式（浮点）"
    return 0, ""

def media_info(path: str) -> dict:
    """当前视频/音频的详细信息（ffprobe -show_format -show_streams 解析）。

    前端「信息」tab 用：文件大小 / 容器 / 时长 / 总码率 / 视频编码/分辨率/帧率/像素格式/位深/色彩/HDR /
    音频编码/采样率/声道/位深/码率 / 容器标签（标题/艺术家等）。
    ★ 不需要任何 Python 第三方包（只用 ffprobe，安装版已随包）。
    """
    from . import ffmpeg_tools
    import subprocess as _sp
    import json as _json

    if not path or not str(path).strip():
        return {"ok": False, "error": "没有正在播放的文件"}
    p = Path(path)
    try:
        if not p.exists():
            return {"ok": False, "error": "文件不存在：" + str(path)}
    except Exception as e:
        return {"ok": False, "error": "路径无效：" + str(e)}

    probe = ffmpeg_tools.find_ffprobe(None, required=False)
    if not probe:
        return {"ok": False, "error": "找不到 ffprobe（安装版应随包提供 third_party/ffmpeg/bin/ffprobe.exe）"}
    # ★ 安装版坑：third_party/ffmpeg/bin/ffprobe.exe 是动态链接版（仅 ~220KB），
    #   它依赖的 av*.dll 放在**应用根目录**（与 dsh_engine 共用），不在 bin 目录里。
    #   不注入 PATH 的话 ffprobe 启动直接失败（Windows 退出码 -1073741515 / 0xC0000135 = 找不到 DLL），
    #   stdout/stderr 全空 → 以前被当成"成功"返回，界面只剩文件大小几个字段。
    import os as _os
    env = dict(_os.environ)
    try:
        app_root = str(DATA_DIR.parent)
        env["PATH"] = app_root + _os.pathsep + env.get("PATH", "")
    except Exception:
        pass
    try:
        r = _sp.run([probe, "-v", "error", "-print_format", "json",
                     "-show_format", "-show_streams", str(p)],
                    capture_output=True, text=True, timeout=120, env=env,
                    encoding="utf-8", errors="replace")
        data = _json.loads(r.stdout or "{}")
        if not data.get("format") and not data.get("streams"):
            tail = (r.stderr or "").strip()[-300:]
            return {"ok": False,
                    "error": "ffprobe 没返回视频信息（退出码 {}）：{}".format(
                        r.returncode, tail if tail else "无输出（可能是 ffprobe 依赖的 DLL 找不到）")}
    except Exception as e:
        return {"ok": False, "error": "ffprobe 解析失败：" + str(e)}

    def _num(x):
        try:
            return float(x)
        except Exception:
            return 0.0

    fmt = data.get("format") or {}
    streams = data.get("streams") or []
    v = next((s for s in streams if s.get("codec_type") == "video"), None)
    a = next((s for s in streams if s.get("codec_type") == "audio"), None)

    out = {
        "ok": True,
        "path": str(p),
        "file_name": p.name,
        "file_size": int(p.stat().st_size) if p.exists() else 0,
        "container": str(fmt.get("format_name") or ""),
        "container_long": str(fmt.get("format_long_name") or ""),
        "duration": _num(fmt.get("duration")),
        "bit_rate": _num(fmt.get("bit_rate")),
        "tags": {str(k): str(vv) for k, vv in (fmt.get("tags") or {}).items()},
        "video": None,
        "audio": None,
    }
    if v:
        fps = 0.0
        for key in ("avg_frame_rate", "r_frame_rate"):
            val = str(v.get(key) or "")
            if "/" in val:
                n, d = val.split("/")[:2]
                try:
                    if _num(d) > 0:
                        fps = _num(n) / _num(d)
                        break
                except Exception:
                    pass
        transfer = str(v.get("color_transfer") or "")
        _vbpr = _num(v.get("bits_per_raw_sample"))
        _vbits, _vbits_kind = _guess_video_bits(str(v.get("pix_fmt") or ""), _vbpr)
        out["video"] = {
            "bit_depth": _vbits,
            "bit_depth_kind": _vbits_kind,
            "codec": str(v.get("codec_name") or ""),
            "codec_long": str(v.get("codec_long_name") or ""),
            "profile": str(v.get("profile") or ""),
            "width": int(_num(v.get("width"))),
            "height": int(_num(v.get("height"))),
            "fps": fps,
            "pix_fmt": str(v.get("pix_fmt") or ""),
            "bit_rate": _num(v.get("bit_rate")),
            "bits_per_raw_sample": _num(v.get("bits_per_raw_sample")),
            "color_primaries": str(v.get("color_primaries") or ""),
            "color_transfer": transfer,
            "color_space": str(v.get("color_space") or ""),
            "color_range": str(v.get("color_range") or ""),
            "hdr": transfer in ("smpte2084", "arib-std-b67"),
        }
    if a:
        _abpr = _num(a.get("bits_per_raw_sample"))
        _abps = _num(a.get("bits_per_sample"))
        _abits, _abits_kind = _guess_audio_bits(str(a.get("sample_fmt") or ""), _abpr, _abps)
        out["audio"] = {
            "bit_depth": _abits,
            "bit_depth_kind": _abits_kind,
            "codec": str(a.get("codec_name") or ""),
            "codec_long": str(a.get("codec_long_name") or ""),
            "profile": str(a.get("profile") or ""),
            "sample_rate": int(_num(a.get("sample_rate"))),
            "channels": int(_num(a.get("channels"))),
            "channel_layout": str(a.get("channel_layout") or ""),
            "bit_rate": _num(a.get("bit_rate")),
            "sample_fmt": str(a.get("sample_fmt") or ""),
            "bits_per_raw_sample": _num(a.get("bits_per_raw_sample")),
        }
    return out

def _yt_cookie_path() -> Path:
    return DATA_DIR / "youtube_cookies.txt"


def get_youtube_cookie_file() -> Optional[str]:
    """YouTube 登录 Cookie 文件路径（Netscape 格式，给 yt-dlp 的 cookiefile 用）；没有则 None。"""
    try:
        p = _yt_cookie_path()
        if p.exists() and p.stat().st_size > 0:
            return str(p)
    except Exception:
        pass
    return None


def get_youtube_cookie_count() -> int:
    try:
        return int(_load_config().get("youtube_cookie_count", 0) or 0)
    except Exception:
        return 0


def _write_netscape_cookies(cookies: list, path: Path) -> int:
    """把 [{name,value,domain,path,secure,expires,httpOnly}] 写成 Netscape Cookie 文件。

    ★ YouTube 的风控识别依赖标准 cookie 文件的 domain/path/secure/过期时间；
      只塞一个裸 Cookie 请求头不够（会被继续判定未登录/风控）。
    """
    lines = ["# Netscape HTTP Cookie File", "# generated by local-media-player (YouTube login)", ""]
    n = 0
    for c in cookies:
        if not isinstance(c, dict):
            continue
        name = str(c.get("name") or "").strip()
        value = str(c.get("value") or "")
        domain = str(c.get("domain") or "").strip()
        if not name or not domain:
            continue
        value = value.replace("\t", "").replace("\r", "").replace("\n", "")
        name = name.replace("\t", "").replace("\r", "").replace("\n", "")
        path_ = str(c.get("path") or "/") or "/"
        secure = "TRUE" if c.get("secure") else "FALSE"
        include_sub = "TRUE" if domain.startswith(".") else "FALSE"
        try:
            expires = int(float(c.get("expires") or 0))
        except Exception:
            expires = 0
        if expires < 0:
            expires = 0
        lines.append("\t".join([domain, include_sub, path_, secure, str(expires), name, value]))
        n += 1
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return n


_yt_verify_cache: dict = {"ts": 0.0, "result": None}


def verify_youtube_login(force: bool = False) -> dict:
    """真·校验 YouTube 登录态：用 cookie 文件请求 youtube.com 首页，检查 "LOGGED_IN":true。

    60 秒缓存（避免界面反复轮询把 YouTube 请求打爆）。
    """
    import time as _t
    now = _t.time()
    if not force and _yt_verify_cache["result"] is not None and now - _yt_verify_cache["ts"] < 60:
        return dict(_yt_verify_cache["result"], cached=True)
    fp = get_youtube_cookie_file()
    if not fp:
        res = {"logged_in": False, "has_cookie": False}
        _yt_verify_cache.update(ts=now, result=res)
        return res
    import re as _re
    import ssl as _ssl
    import urllib.request as _ur
    rows = []
    try:
        for line in Path(fp).read_text(encoding="utf-8").splitlines():
            if not line or line.startswith("#"):
                continue
            p = line.split("\t")
            if len(p) >= 7:
                rows.append({"domain": p[0], "name": p[5], "value": p[6]})
    except Exception as e:
        res = {"logged_in": False, "has_cookie": True, "error": "读取 cookie 文件失败：" + str(e)}
        _yt_verify_cache.update(ts=now, result=res)
        return res
    yt = [r for r in rows if "youtube" in r["domain"]]
    cookie = "; ".join(r["name"] + "=" + r["value"] for r in yt)
    req = _ur.Request("https://www.youtube.com/", headers={
        "Cookie": cookie,
        "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                       "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"),
        "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
    })
    try:
        with _ur.urlopen(req, timeout=25, context=_ssl.create_default_context()) as r:
            html = r.read().decode("utf-8", errors="replace")
        m = _re.search(r'"LOGGED_IN"\s*:\s*(true|false)', html)
        logged = bool(m and m.group(1) == "true")
        res = {"logged_in": logged, "has_cookie": True, "cookie_rows": len(rows),
               "youtube_rows": len(yt)}
    except Exception as e:
        res = {"logged_in": False, "has_cookie": True, "error": "校验请求失败：" + str(e)[:200]}
    _yt_verify_cache.update(ts=now, result=res)
    return res

def set_youtube_cookie(payload) -> dict:
    """保存 YouTube 登录 Cookie。

    payload 可以是：
      · dict {"cookies": [{name,value,domain,path,secure,expires,httpOnly}, ...]}（登录窗口抓的）
      · JSON 字符串（同上结构）
      · 旧版整串 "name=value; name2=value2"（会补 .youtube.com 域）
    """
    import json as _json
    cookies: list = []
    raw_str = ""
    try:
        data = payload
        if isinstance(payload, str):
            raw_str = payload.strip()
            if raw_str.startswith("{"):
                data = _json.loads(raw_str)
        if isinstance(data, dict):
            cookies = data.get("cookies") or []
            if not cookies and isinstance(data.get("cookie"), str):
                raw_str = data["cookie"].strip()
                # ★ 前端传的是 JSON 字符串（{cookies:[...]}）→ 再解析一次
                if raw_str.startswith("{"):
                    try:
                        _parsed = _json.loads(raw_str)
                        if isinstance(_parsed, dict) and _parsed.get("cookies"):
                            cookies = _parsed["cookies"]
                    except Exception:
                        pass
        elif isinstance(data, list):
            cookies = data
    except Exception:
        cookies = []
    if not cookies and raw_str and "=" in raw_str:
        for part in raw_str.split(";"):
            if "=" in part:
                nm, vl = part.split("=", 1)
                cookies.append({"name": nm.strip(), "value": vl.strip(),
                                "domain": ".youtube.com", "path": "/",
                                "secure": True, "httpOnly": False, "expires": 0})
    p = _yt_cookie_path()
    if not cookies:
        try:
            if p.exists():
                p.unlink()
        except Exception:
            pass
        _update_config(lambda cfg: {**cfg, "youtube_cookie_count": 0})
        return {"ok": True, "has_cookie": False, "count": 0}
    n = _write_netscape_cookies(cookies, p)
    _update_config(lambda cfg: {**cfg, "youtube_cookie_count": n})
    # ★ 保存后立刻做真·登录校验（避免"看着像登录了其实没登录"——用户就踩过这个坑：
    #   只抓到 Google 域 Cookie、YouTube 域没有登录态，解析照样被判 bot）
    try:
        chk = verify_youtube_login(force=True)
    except Exception:
        chk = {}
    return {"ok": True, "has_cookie": n > 0, "count": n,
            "logged_in": bool(chk.get("logged_in")), "verify": chk}

def parse_generic(url: str) -> dict:
    """非 B 站链接（YouTube / Twitter / 直链 mp4 …）通用解析：yt-dlp extract_info。

    返回结构与 B 站解析尽量一致，前端只需看 is_bilibili 区分：
      formats 里每项给 **可直接传给 yt-dlp 的 format 选择串**（如 bv*[height<=1080]+ba/b），
      前端选哪项就把该项的 format_id 当 format_sel 提交，后端通用通道原样使用。
    """
    try:
        from . import downloader
        info = downloader.parse_url_fallback(url, cookiefile=get_youtube_cookie_file())
    except ImportError:
        return {"error": "安装包缺少 yt-dlp（YouTube 等站点解析/下载需要它）", "is_bilibili": False}
    except Exception as e:
        msg = str(e)
        hint = ""
        low = msg.lower()
        if any(k in low for k in ("timed out", "connection", "resolve", "ssl", "urlopen",
                                  "network", "proxy", "getaddrinfo", "unable to download")):
            hint = "（本机访问不了该站点：YouTube 等需要能上外网/代理）"
        return {"error": "解析失败：" + msg[:300] + hint, "is_bilibili": False}

    # ★ 播放列表（YouTube list=…）：先按列表解析，返回列表信息 + 可选画质
    try:
        _is_list_url = ("list=" in url) or ("/playlist" in url)
        if _is_list_url:
            pl = downloader.parse_playlist(url, limit=500, cookiefile=get_youtube_cookie_file())
            if pl.get("is_playlist"):
                # ★ 列表里每一集的可用清晰度都不一样（实测：第 1 集有 2160/1440，第 2 集起最高只有 1080），
                #   所以这里只给"通用梯子"当初始值，前端随后会逐条调 /api/bilibili/parse 拿该集真实清晰度刷新下拉。
                _pl_choices = []
                for _h in (2160, 1440, 1080, 720, 480, 360, 240, 144):
                    _note = "{}P".format(_h)
                    if _h >= 2160:
                        _note = "{}P（4K）".format(_h)
                    elif _h >= 1440:
                        _note = "{}P（2K）".format(_h)
                    _pl_choices.append({
                        "format_id": "bv*[height<={0}]+ba/b[height<={0}]/b".format(_h),
                        "height": _h, "note": _note, "ext": "mp4",
                    })
                _pl_choices.append({"format_id": "bv*+ba/b", "height": 0,
                                    "note": "最佳画质（自动）", "ext": "mp4"})
                return {
                    "is_bilibili": False,
                    "is_playlist": True,
                    "playlist_title": pl.get("title") or "",
                    "playlist_count": int(pl.get("count") or 0),
                    "playlist_entries": (pl.get("entries") or [])[:300],
                    "title": pl.get("title") or "",
                    "uploader": pl.get("uploader") or "",
                    "duration": 0,
                    "webpage_url": url,
                    "thumbnail": "",
                    "bvid": "",
                    "formats": _pl_choices,
                    "quality": 0,
                    "hires_available": False,
                    "already_downloaded": False,
                }
    except Exception as _e:
        logger.warning("播放列表解析失败，按单视频处理: %s", str(_e)[:160])
    # 按分辨率聚合出可选画质（每档给出可直接用的 yt-dlp format 串）
    heights = set()
    for f in info.get("formats") or []:
        h = f.get("height")
        if h:
            heights.add(int(h))
    choices = []
    for h in sorted(heights, reverse=True):
        choices.append({
            "format_id": "bv*[height<={0}]+ba/b[height<={0}]/b".format(h),
            "height": h,
            "note": "{}P".format(h),
            "ext": "mp4",
        })
    choices.append({"format_id": "bv*+ba/b", "height": 0, "note": "最佳画质（自动）", "ext": "mp4"})
    return {
        "is_bilibili": False,
        "title": info.get("title", ""),
        "uploader": info.get("uploader", "") or "",
        "duration": info.get("duration", 0) or 0,
        "webpage_url": info.get("webpage_url", url),
        "thumbnail": info.get("thumbnail", "") or "",
        "bvid": "",
        "is_playlist": bool(info.get("is_playlist")),
        "formats": choices,
        "quality": 0,
        "hires_available": False,
        "already_downloaded": False,
    }

# 增强类型显示名（版本编号用）
_ENHANCER_NAMES = {
    "realesrgan": "画质",
    "denoise": "降噪",
    "loudness": "响度统一",
    "audio_sr": "音频超分",
    "soxr_hires": "Hi-Res重采样",
    "fft_hires": "FFT重采样",
    "rife": "补帧",
}


def _version_tree(rows: list[dict]) -> list[dict]:
    """给一组增强任务计算版本名 + 血缘树排序（不按时间）。

    命名规则：原版（原始媒体）→ 每个增强 = 父版本名 + 增强名 + #序号。
      例：原版 → 降噪#1 → 降噪#1 响度统一#1；对原版再降噪 → 降噪#2。
    排序：血缘树先序遍历（父在前、子紧跟其后），同父同类型按 #序号。
    """
    # id -> task dict
    by_id = {r["id"]: dict(r) for r in rows}
    # children[parent_id] = [task...]（parent_id 0 = 原始媒体根）
    children: dict[int, list[dict]] = {}
    for r in rows:
        children.setdefault(r.get("parent_id") or 0, []).append(r)
    # 同父任务按 id 升序（稳定，等价于创建顺序）
    for k in children:
        children[k].sort(key=lambda t: t["id"])

    # 预计算：同一父节点下、同一增强类型的序号
    seq: dict[tuple[int, str], int] = {}
    for parent_id, lst in children.items():
        counts: dict[str, int] = {}
        for t in lst:
            enh = t["enhancer"]
            counts[enh] = counts.get(enh, 0) + 1
            seq[(t["id"], parent_id)] = counts[enh]

    def name_of(t: dict) -> str:
        parent_id = t.get("parent_id") or 0
        parent = by_id.get(parent_id) if parent_id else None
        enh = _ENHANCER_NAMES.get(t["enhancer"], t["enhancer"])
        n = seq.get((t["id"], parent_id), 1)
        seg = "{}#{}".format(enh, n)
        if parent:
            # 链式：父版本名 + 增强名#序号（例：降噪#1 响度统一#1）
            return "{} {}".format(name_of(parent), seg)
        # 直接来自原版：就叫「降噪#1」（不重复"原版"前缀）
        return seg

    # 先序遍历：根（原版）→ 其子 → 孙...
    ordered: list[dict] = []

    def walk(parent_id: int, depth: int = 0) -> None:
        for t in children.get(parent_id, []):
            t["_name"] = name_of(t)
            t["_depth"] = depth
            ordered.append(t)
            walk(t["id"], depth + 1)

    walk(0)
    return ordered


_pdf_sr_lock = threading.Lock()
_pdf_sr_cache: dict = {}


def upscale_pdf_image(image_data_url: str, scale: int = 2) -> dict:
    """PDF 页面超分（Real-ESRGAN）：输入 data URL，返回放大后的 PNG data URL。

    仅供 PDF 阅读器扩展调用；GPU 推理串行化，避免并发请求同时吃显存。
    """
    import base64

    if not isinstance(image_data_url, str) or "," not in image_data_url:
        raise ValueError("缺少页面图片")
    head, b64 = image_data_url.split(",", 1)
    if not head.startswith("data:image/") or ";base64" not in head:
        raise ValueError("页面图片必须是 base64 data URL")
    try:
        data = base64.b64decode(b64, validate=True)
    except Exception as e:
        raise ValueError("页面图片 base64 解码失败") from e
    if len(data) > 30 * 1024 * 1024:
        raise ValueError("页面图片过大（超过 30MB）")
    try:
        scale = int(scale)
    except Exception:
        raise ValueError("倍率必须是 2 或 4")
    if scale not in (2, 4):
        raise ValueError("倍率只支持 2 或 4")
    t0 = time.monotonic()
    from .enhancers.realesrgan import RealesrganEnhancer

    # 同一进程缓存模型；锁住整个推理，PDF 逐页请求不会同时抢 GPU。
    with _pdf_sr_lock:
        enh = _pdf_sr_cache.get(scale)
        if enh is None:
            enh = RealesrganEnhancer(device="cuda", model_name="RealESRGAN_x4plus", scale=scale)
            _pdf_sr_cache[scale] = enh
        out = enh.enhance_image_bytes(data, scale)
    return {
        "ok": True,
        "image": "data:image/png;base64," + base64.b64encode(out).decode("ascii"),
        "scale": scale,
        "bytes": len(out),
        "elapsed_ms": round((time.monotonic() - t0) * 1000, 1),
    }


def list_enhanced_versions(media_type: str, media_id: int) -> dict:
    """某媒体的已完成增强版本（血缘树排序，带版本名，供播放器版本菜单）。

    ★ T27：每项补 `enhancers`（节点序列）/`chain_name`（链名）/`source_title`（源标题）。
    """
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, media_id, media_type, enhancer, enhancers_json, output_path, source_path, "
            "parent_id, created_at "
            "FROM enhance_tasks "
            "WHERE media_type=? AND media_id=? AND status='done' AND output_path!=''",
            (media_type, media_id),
        ).fetchall()
    source_title = _media_title(media_type, media_id)
    tree = _version_tree([dict(r) for r in rows])
    versions = []
    for t in tree:
        nodes = _task_enhancers(t)
        versions.append({
            "id": t["id"],
            "enhancer": t["enhancer"],
            "name": t["_name"],
            "depth": t["_depth"],
            "output_path": t["output_path"],
            "source_path": t["source_path"],
            "parent_id": t["parent_id"],
            # ★ T27 扩展字段（前端 T28 按契约读）；nodes 带参数
            "enhancers": [_node_key(n) for n in nodes],
            "nodes": nodes,
            "node_details": _node_display(nodes),        # ★ 血统/版本列表：带参数
            "chain_name": _chain_name(nodes),
            "chain_name_detail": _chain_name_detail(nodes),
            "source_title": source_title,
        })
    return {"versions": versions}


def list_all_enhanced() -> dict:
    """全部已完成增强版本（增强页「增强版本」tab：链式增强输入，血缘名）。"""
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, media_id, media_type, enhancer, enhancers_json, output_path, source_path, "
            "parent_id, created_at "
            "FROM enhance_tasks WHERE status='done' AND output_path!='' "
            "ORDER BY media_type, media_id DESC, id DESC LIMIT 500"
        ).fetchall()
    # 按 (media_type, media_id) 分组各自算版本树
    groups: dict[tuple, list[dict]] = {}
    for r in rows:
        groups.setdefault((r["media_type"], r["media_id"]), []).append(dict(r))
    versions = []
    for (mt, mid), grp_rows in groups.items():
        title = _media_title(mt, mid)
        for t in _version_tree(grp_rows):
            nodes = _task_enhancers(t)
            versions.append({
                "id": t["id"],
                "media_id": t["media_id"],
                "media_type": t["media_type"],
                "enhancer": t["enhancer"],
                "name": t["_name"],
                "depth": t["_depth"],
                "media_title": title,
                "output_path": t["output_path"],
                "source_path": t["source_path"],
                "parent_id": t["parent_id"],
                # ★ T27 扩展字段；nodes 带参数
                "enhancers": [_node_key(n) for n in nodes],
                "nodes": nodes,
                "node_details": _node_display(nodes),    # ★ 血统/版本列表：带参数
                "chain_name": _chain_name(nodes),
                "chain_name_detail": _chain_name_detail(nodes),
                "source_title": title,
            })
    return {"versions": versions}


def product_info(path: str) -> dict:
    """当前播放的文件是不是增强产物？是的话把它依托的源视频信息一并返回。

    用途：播放增强版时，主界面要在标题下面显示「源视频的简介/发布日期/作者」，并提供「返回源视频」。
    返回 {"is_product": bool, "task": {...}, "source": {...}} 或 {"is_product": False}。
    """
    p = str(path or "").strip()
    if not p:
        return {"is_product": False}
    row = None
    with get_conn() as c:
        row = c.execute(
            "SELECT * FROM enhance_tasks WHERE output_path=? ORDER BY id DESC LIMIT 1", (p,)
        ).fetchone()
        if not row:
            # Windows 路径大小写/斜杠不敏感：拿所有已完成任务做一次规范化比对（文件不多，不慢）
            try:
                cand = c.execute(
                    "SELECT * FROM enhance_tasks WHERE status='done' AND output_path!='' ORDER BY id DESC LIMIT 300"
                ).fetchall()
                np = os.path.normcase(os.path.normpath(p))
                for r in cand:
                    if os.path.normcase(os.path.normpath(str(r["output_path"] or ""))) == np:
                        row = r
                        break
            except Exception:
                row = None
        if not row:
            return {"is_product": False}
        task = dict(row)
        media_type = task.get("media_type") or "video"
        mid = int(task.get("media_id") or 0)
        nodes = _task_enhancers(task)
        task_out = {
            "id": int(task["id"]),
            "media_id": mid,
            "media_type": media_type,
            "chain_name": _chain_name(nodes),
            "chain_name_detail": _chain_name_detail(nodes),
            "node_details": _node_display(nodes),
            "created_at": task.get("created_at") or "",
            "output_path": task.get("output_path") or "",
            "source_path": task.get("source_path") or "",
            "parent_id": int(task.get("parent_id") or 0),
        }
        source = None
        if media_type == "audio":
            srow = c.execute(
                "SELECT id, title, artist, album, cover_path, file_path, created_at FROM songs WHERE id=?",
                (mid,),
            ).fetchone()
            if srow:
                source = {
                    "id": int(srow["id"]), "kind": "audio",
                    "title": srow["title"] or "", "up_name": srow["artist"] or "",
                    "desc": "", "pub_time": 0, "bvid": "", "cover_path": srow["cover_path"] or "",
                    "local_path": srow["file_path"] or "", "created_at": srow["created_at"] or "",
                }
        else:
            srow = c.execute(
                "SELECT id, title, up_name, desc, pub_time, bvid, cover_path, local_path, created_at "
                "FROM videos WHERE id=?", (mid,),
            ).fetchone()
            if srow:
                source = {
                    "id": int(srow["id"]), "kind": "video",
                    "title": srow["title"] or "", "up_name": srow["up_name"] or "",
                    "desc": srow["desc"] or "", "pub_time": int(srow["pub_time"] or 0),
                    "bvid": srow["bvid"] or "", "cover_path": srow["cover_path"] or "",
                    "local_path": srow["local_path"] or "", "created_at": srow["created_at"] or "",
                }
        return {"is_product": True, "task": task_out, "source": source}


def delete_enhance_task(task_id: int) -> dict:
    """删除一条增强任务：删除记录 + 删除输出文件（增强版删除按钮）。

    ★ T27 删除保护：若有其它 task 的 parent_id 指向它（= 有子代增强版本基于它），
      返回 {"conflict": True, "error": ...}，由 HTTP 层映射成 **409**，且**不删文件**（保留血缘）。
    """
    with get_conn() as c:
        children = c.execute(
            "SELECT COUNT(*) AS n FROM enhance_tasks WHERE parent_id=?", (task_id,)
        ).fetchone()
        n_child = int(children["n"] if children else 0)
        if n_child > 0:
            msg = "有 {} 个增强版本基于它，不能删除（会断开血缘）".format(n_child)
            logger.info("拒绝删除增强任务 #%s：%s", task_id, msg)
            return {"conflict": True, "error": msg, "children": n_child}
        row = c.execute("SELECT output_path FROM enhance_tasks WHERE id=?", (task_id,)).fetchone()
        output = (row["output_path"] or "") if row else ""
        c.execute("DELETE FROM enhance_tasks WHERE id=?", (task_id,))
    if output:
        try:
            Path(output).unlink(missing_ok=True)
        except Exception:
            logger.warning("删除增强输出文件失败: %s", output)
    return {"ok": True}

def video_intro(bvid: str, page: int = 0) -> dict:
    """取视频简介。

    库里 videos.desc 早期存的是**网页地址**（下载时写的 webpage_url），不是真简介
    → 这里判断：空 或 看着像网址 → 去 B站 view 接口抓一次真简介，并**缓存回 desc**；
    下次直接命中缓存（不再联网）。返回 {"desc": ..., "cached": bool}。
    """
    if not bvid:
        return {"desc": "", "cached": False}
    cur = ""
    with get_conn() as c:
        row = c.execute("SELECT desc FROM videos WHERE bvid=? ORDER BY id DESC LIMIT 1", (bvid,)).fetchone()
        cur = (row["desc"] if row else "") or ""
    looks_like_url = cur.strip().lower().startswith(("http://", "https://"))
    if cur.strip() and not looks_like_url:
        return {"desc": cur, "cached": True}
    try:
        info = bilibili.get_video_info(bvid)
        desc = (info.get("desc") or "").strip()
    except Exception as e:
        logger.warning("取简介失败 bvid=%s: %s", bvid, e)
        return {"desc": "" if looks_like_url else cur, "cached": False, "error": str(e)}
    if desc:
        with get_conn() as c:
            c.execute("UPDATE videos SET desc=? WHERE bvid=?", (desc, bvid))
    return {"desc": desc, "cached": False}

def _store_replies(c, video_id: int, top_rpid: int, replies: list) -> int:
    """把楼中楼子评论写库（reply_to 记录被回复者，root 记录顶层 rpid）。返回写入条数。"""
    n = 0
    for r in replies:
        if not r.get("rpid"):
            continue
        c.execute(
            "INSERT OR IGNORE INTO comments (video_id, rpid, user_name, content, like_count, created_at, reply_to, root, reply_count) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0)",
            (video_id, r["rpid"], r.get("user_name", ""), r.get("content", ""),
             r.get("like_count", 0), r.get("created_at", 0), r.get("parent") or top_rpid, top_rpid),
        )
        n += 1
    return n


def comment_replies(vid: int, rpid: int, page: int = 1) -> dict:
    """某条评论的完整"楼中楼"：先去 B站抓这一页（并入库），再返回库里该楼全部回复。

    B站接口：/x/v2/reply/reply（需要 wbi 签名），一页 20 条。
    """
    page = max(1, page)
    with get_conn() as c:
        row = c.execute("SELECT bvid FROM videos WHERE id=?", (vid,)).fetchone()
        before = c.execute("SELECT COUNT(*) FROM comments WHERE video_id=? AND root=?", (vid, rpid)).fetchone()[0]
    fetched, err = 0, ""
    if row and row["bvid"]:
        try:
            rs = bilibili.fetch_comment_replies(row["bvid"], rpid, page=page)
            with get_conn() as c:
                fetched = _store_replies(c, vid, rpid, rs)
        except Exception as e:
            err = str(e)
            logger.warning("抓楼中楼失败 vid=%s rpid=%s: %s", vid, rpid, e)
    with get_conn() as c:
        rows = c.execute(
            "SELECT rpid, user_name, content, like_count, created_at, reply_to FROM comments "
            "WHERE video_id=? AND root=? ORDER BY like_count DESC, created_at ASC",
            (vid, rpid),
        ).fetchall()
        top = c.execute("SELECT reply_count FROM comments WHERE video_id=? AND rpid=?", (vid, rpid)).fetchone()
    return {"replies": [dict(r) for r in rows], "count": len(rows),
            "reply_count": (top["reply_count"] if top else 0),
            "stored_before": before, "fetched": fetched, "page": page, "error": err}
