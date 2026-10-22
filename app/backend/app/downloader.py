"""yt-dlp 封装：解析 B 站视频信息与下载（完整实现）。

说明：B 站视频需要 ffmpeg 合流（yt-dlp 下载最佳视频流+音频流后合并）。
T24：ffmpeg 路径统一走 `app/ffmpeg_tools.py`（FFMPEG_PATH → 常见位置 → PATH），
本模块以前是唯一支持 FFMPEG_PATH 的地方，现在全项目共用一个解析器。
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Any, Callable, Optional

import yt_dlp

from . import ffmpeg_tools
from .jsruntime import js_runtime_opts

logger = logging.getLogger(__name__)

# ffmpeg 探测：统一解析器（import 期求值 → 用 required=False，解析不到给 None，不抛）
FFMPEG_LOCATION = ffmpeg_tools.find_ffmpeg(required=False) or None

INFO_OPTS: dict[str, Any] = {
    "skip_download": True,
    "quiet": True,
    "no_warnings": True,
    # ★ 2026-10-18：给解析加网络超时与重试上限 —— 以前 YouTube 慢/代理卡时可能无限等
    "socket_timeout": 20,
    "retries": 2,
    "extractor_retries": 2,
    "noplaylist": True,
}

# 下载默认选择：最佳视频流+最佳音频流合并 mp4，失败回退单一最佳
DEFAULT_FORMAT = "bv*+ba/b"


# ★ YouTube 间歇性风控："Sign in to confirm you're not a bot"
#   对策：自动换 android 客户端重试一次（android 通常不需要登录验证）
_BOT_PATTERNS = (
    "sign in to confirm", "not a bot", "confirm you're not a bot",
    "please sign in", "login required", "cookies for the authentication",
    "this content isn't available", "requires authentication",
)


def fetch_yt_subtitles(url: str, video_path: Path, base_opts: Optional[dict[str, Any]] = None) -> dict:
    """给**已下载**的 YouTube 视频补字幕：先抓全部人工字幕，再抓自动生成字幕（.auto. 标记）。

    返回 {"manual": 个数, "auto": 个数, "error": ""}
    """
    opts = dict(INFO_OPTS)
    opts.update(js_runtime_opts())
    try:
        from . import services as _svc
        cf = _svc.get_youtube_cookie_file()
        if cf:
            opts["cookiefile"] = cf
    except Exception:
        pass
    if base_opts:
        opts.update(base_opts)
    manual = 0
    try:
        mid_opts = dict(opts)
        mid_opts.update({
            "skip_download": True,
            "writesubtitles": True,
            "writeautomaticsub": False,
            "subtitleslangs": ["all"],
            "subtitlesformat": "srt",
            "outtmpl": str(video_path.parent / (video_path.stem + ".%(ext)s")),
        })
        with yt_dlp.YoutubeDL(mid_opts) as ydl:
            ydl.extract_info(url, download=True)
        manual = len(list(video_path.parent.glob(video_path.stem + ".*.srt")))
        logger.info("补人工字幕完成：%d 个文件", manual)
    except Exception as e:
        logger.warning("补人工字幕失败: %s", str(e)[:160])
    before = set(str(x) for x in video_path.parent.glob(video_path.stem + "*.srt"))
    try:
        _ensure_subtitles(url, video_path, opts)
    except Exception as e:
        logger.warning("补自动字幕失败: %s", str(e)[:160])
    after = set(str(x) for x in video_path.parent.glob(video_path.stem + "*.srt"))
    auto_files = [x for x in (after - before) if ".auto." in x]
    return {"manual": manual, "auto": len(auto_files), "error": ""}

def _ensure_subtitles(url: str, path: Path, base_opts: dict[str, Any]) -> None:
    """人工字幕已由主下载抓取；这里再把 YouTube **自动生成字幕**也抓下来（两者都要，可切换）。

    ★ 人工/自动的字幕文件命名会撞车（同一个语言代码，如 ko 两边都有）→ 自动字幕先下到临时目录，
      再统一改名成 `<视频名>.auto.<语言>.srt` 放到视频旁边，这样两套共存、前端能区分标注。
    """
    import tempfile as _tf
    import time as _t
    # 少请求几门语言，降低被 YouTube 限流（429）的概率
    auto_langs = ["zh-Hans", "zh-Hant", "en", "ko", "ja"]
    for attempt in (1, 2):
        tmpdir = ""
        try:
            tmpdir = _tf.mkdtemp(prefix="dshtv_auto_sub_")
            auto_opts = dict(base_opts)
            auto_opts.update({
                "skip_download": True,
                "writesubtitles": False,
                "writeautomaticsub": True,
                "subtitleslangs": auto_langs,
                "subtitlesformat": "srt",
                "outtmpl": str(Path(tmpdir) / "%(title)s.%(ext)s"),
            })
            with yt_dlp.YoutubeDL(auto_opts) as ydl:
                ydl.extract_info(url, download=True)
            moved = 0
            for f in Path(tmpdir).glob("*.srt"):
                stem = f.stem
                lang = stem.rsplit(".", 1)[-1] if "." in stem else "auto"
                dst = path.parent / "{}.auto.{}.srt".format(path.stem, lang)
                try:
                    f.replace(dst)
                    moved += 1
                except Exception:
                    pass
            if moved:
                logger.info("自动生成字幕已保存 %d 个（.auto. 标记）", moved)
                return
            logger.warning("自动字幕没抓到（可能被限流，尝试 %d/2）", attempt)
        except Exception as e:
            logger.warning("抓自动字幕失败（尝试 %d/2）: %s", attempt, str(e)[:160])
        finally:
            if tmpdir:
                try:
                    import shutil as _sh
                    _sh.rmtree(tmpdir, ignore_errors=True)
                except Exception:
                    pass
        if attempt == 1:
            _t.sleep(4)   # 被限流时隔几秒重试一次


def _pub_time_of(info: dict) -> int:
    """从 yt-dlp info 里取视频发布日期（Unix 秒）：timestamp / release_timestamp / upload_date(YYYYMMDD)。"""
    try:
        ts = info.get("release_timestamp") or info.get("timestamp")
        if ts:
            return int(ts)
        ud = str(info.get("upload_date") or "")
        if len(ud) == 8 and ud.isdigit():
            import datetime as _dt
            return int(_dt.datetime(int(ud[:4]), int(ud[4:6]), int(ud[6:8]),
                                    tzinfo=_dt.timezone.utc).timestamp())
    except Exception:
        pass
    return 0

def _is_bot_error(msg: str) -> bool:
    low = (msg or "").lower()
    return any(k in low for k in _BOT_PATTERNS)


def _android_opts() -> dict[str, Any]:
    return {"extractor_args": {"youtube": {"player_client": ["android"]}}}

def parse_url(url: str, extra_opts: Optional[dict[str, Any]] = None) -> dict[str, Any]:
    """解析链接，返回视频信息（标题/UP主/封面/分P/清晰度等）。"""
    opts = dict(INFO_OPTS)
    opts.update(js_runtime_opts())   # ★ YouTube JS 挑战需要真实 JS 运行时（node）
    # ★ 字幕：**只抓人工字幕**（视频作者/社区上传的多语言 CC），不再把 YouTube 自动机翻的也下下来。
    #   没有人工字幕的视频，下载完再单独补一次自动字幕（见 _ensure_subtitles）。
    opts.update({
        "writesubtitles": True,
        "writeautomaticsub": False,
        "subtitleslangs": ["all"],
        "subtitlesformat": "srt",
    })
    if extra_opts:
        opts.update(extra_opts)
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False)

    formats: list[dict[str, Any]] = []
    seen: set[str] = set()
    for f in info.get("formats", []):
        if f.get("vcodec") == "none" and f.get("acodec") == "none":
            continue
        key = (f.get("format_id"), f.get("height"))
        if key in seen:
            continue
        seen.add(key)
        formats.append({
            "format_id": f.get("format_id"),
            "ext": f.get("ext"),
            "height": f.get("height"),
            "vcodec": f.get("vcodec"),
            "acodec": f.get("acodec"),
            "filesize": f.get("filesize"),
            "note": f.get("format_note"),
        })

    return {
        "title": info.get("title", ""),
        "uploader": info.get("uploader", ""),
        "duration": info.get("duration", 0),
        "webpage_url": info.get("webpage_url", url),
        "thumbnail": info.get("thumbnail", ""),
        "bvid": info.get("id", ""),
        "cid": info.get("cid"),  # 分P编号（弹幕抓取用）
        "is_playlist": info.get("_type") == "playlist",
        "formats": formats[:30],
    }


def parse_playlist(url: str, limit: int = 500, cookiefile: Optional[str] = None) -> dict[str, Any]:
    """解析播放列表（YouTube list= / 其它站点的 playlist），只取扁平信息（快）。

    返回 {"is_playlist": bool, "title", "uploader", "count", "entries": [{title,url,duration,index}]}
    """
    opts = dict(INFO_OPTS)
    opts.update(js_runtime_opts())
    opts.update({
        "noplaylist": False,
        "extract_flat": "in_playlist",
        "playlistend": limit,
        "skip_download": True,
    })
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False)
    if not info:
        return {"is_playlist": False, "title": "", "uploader": "", "count": 0, "entries": []}
    if info.get("_type") != "playlist":
        # 不是播放列表（单个视频）→ 返回单条
        return {"is_playlist": False,
                "title": info.get("title") or "",
                "uploader": info.get("uploader") or info.get("channel") or "",
                "count": 1,
                "entries": [{"title": info.get("title") or "",
                             "url": info.get("webpage_url") or url,
                             "duration": info.get("duration") or 0,
                             "index": 1}],
                "pub_time": _pub_time_of(info)}
    entries = []
    for i, e in enumerate(info.get("entries") or []):
        if not e:
            continue
        url_e = e.get("webpage_url") or e.get("url") or ""
        if not url_e and e.get("id"):
            url_e = "https://www.youtube.com/watch?v=" + str(e["id"])
        if not url_e:
            continue
        entries.append({
            "title": e.get("title") or "",
            "url": url_e,
            "duration": e.get("duration") or 0,
            "index": i + 1,
        })
    return {
        "is_playlist": len(entries) > 1,
        "title": info.get("title") or "",
        "uploader": info.get("uploader") or info.get("channel") or "",
        "count": len(entries),
        "entries": entries,
        "pub_time": 0,
    }

def parse_url_fallback(url: str, cookie: Optional[str] = None, cookiefile: Optional[str] = None) -> dict[str, Any]:
    """解析链接：先常规客户端（带 YouTube 登录 Cookie 时无风控问题）；
    命中 YouTube bot 风控时自动换 android 客户端重试一次。
    """
    cookie_opts: dict[str, Any] = {}
    if cookie:
        cookie_opts["http_headers"] = {"Cookie": cookie}
    if cookiefile:
        cookie_opts["cookiefile"] = cookiefile
    try:
        return parse_url(url, cookie_opts or None)
    except Exception as e:
        msg = str(e)
        if _is_bot_error(msg):
            logger.warning("解析命中 YouTube 风控 → 换 android 客户端重试: %s", msg[:160])
            try:
                extra = dict(cookie_opts)
                extra.update(_android_opts())
                return parse_url(url, extra)
            except Exception as e2:
                raise RuntimeError(
                    "YouTube 风控拦截（Sign in to confirm you're not a bot），"
                    "android 客户端兜底也失败：{}".format(str(e2)[:200])
                ) from e2
        raise

def download(
    url: str,
    out_dir: Path,
    format_sel: str = DEFAULT_FORMAT,
    progress_cb: Optional[Callable[[float], None]] = None,
    cookies: Optional[str] = None,
    cookies_from_browser: Optional[str] = None,
    player_client: Optional[str] = None,
    cookiefile: Optional[str] = None,
    info_out: Optional[dict] = None,
) -> Path:
    """下载视频到 out_dir，返回成品文件路径。

    cookies: 完整 Cookie 字符串（'SESSDATA=...; bili_jct=...'），直接作为请求头
             （解锁大会员高清 1080p+/4K，无需读取浏览器）。
    cookies_from_browser: 浏览器名（'chrome'/'edge'），自动读取其登录态（需浏览器关闭）。
    """
    out_dir.mkdir(parents=True, exist_ok=True)

    opts: dict[str, Any] = {
        "outtmpl": str(out_dir / "%(title)s.%(ext)s"),
        "format": format_sel,
        "merge_output_format": "mp4",
        "quiet": True,
        "no_warnings": True,
        "noplaylist": True,
        "retries": 3,
        "fragment_retries": 3,
        "overwrites": True,   # 重复下载覆盖同名文件（避免产生 "xxx (1).mp4" 副本）
    }
    if cookies:
        opts.setdefault("http_headers", {})["Cookie"] = cookies
    elif cookies_from_browser:
        opts["cookiesfrombrowser"] = (cookies_from_browser, None, None, None)
    if player_client:
        opts["extractor_args"] = {"youtube": {"player_client": [player_client]}}
    if cookiefile:
        opts["cookiefile"] = cookiefile
    opts.update(js_runtime_opts())   # ★ YouTube JS 挑战需要真实 JS 运行时（node）
    # ★ 字幕：**只抓人工字幕**（视频作者/社区上传的多语言 CC），不再把 YouTube 自动机翻的也下下来。
    #   没有人工字幕的视频，下载完再单独补一次自动字幕（见 _ensure_subtitles）。
    opts.update({
        "writesubtitles": True,
        "writeautomaticsub": False,
        "subtitleslangs": ["all"],
        "subtitlesformat": "srt",
    })
    if FFMPEG_LOCATION:
        opts["ffmpeg_location"] = FFMPEG_LOCATION

    if progress_cb:
        def hook(d: dict[str, Any]) -> None:
            if d.get("status") == "downloading":
                total = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
                if total:
                    progress_cb(d.get("downloaded_bytes", 0) / total)
            elif d.get("status") == "finished":
                progress_cb(1.0)
        opts["progress_hooks"] = [hook]

    def _run(o: dict[str, Any]) -> Path:
        with yt_dlp.YoutubeDL(o) as ydl:
            info = ydl.extract_info(url, download=True)
            path = Path(ydl.prepare_filename(info))
            # ★ 回传视频元数据（非 B 站下载入库用：标题/频道/简介/时长/封面/分辨率）
            if info_out is not None:
                try:
                    reqs = info.get("requested_formats") or []
                    heights = [int(f.get("height") or 0) for f in reqs
                               if f.get("vcodec") and f.get("vcodec") != "none"]
                    h = max(heights) if heights else int(info.get("height") or 0)
                    info_out.update({
                        "title": info.get("title") or "",
                        "uploader": info.get("uploader") or info.get("channel") or "",
                        "description": info.get("description") or "",
                        "duration": int(round((info.get("duration") or 0) * 1000)),   # 毫秒
                        "thumbnail": info.get("thumbnail") or "",
                        "height": h,
                        "webpage_url": info.get("webpage_url") or url,
                        "extractor": info.get("extractor") or "",
                        "view_count": int(info.get("view_count") or 0),
                        "pub_time": _pub_time_of(info),
                    })
                except Exception as _e:
                    logger.warning("回传 yt-dlp info 失败: %s", _e)
            merged = path.with_suffix(".mp4")
            return merged if merged.exists() else path

    try:
        result = _run(opts)
    except Exception as e:
        # ★ YouTube 间歇性风控（Sign in to confirm you're not a bot）→ 换 android 客户端重试一次
        if _is_bot_error(str(e)):
            logger.warning("下载命中 YouTube 风控 → 换 android 客户端重试: %s", str(e)[:160])
            opts2 = dict(opts)
            opts2["extractor_args"] = {"youtube": {"player_client": ["android"]}}
            result = _run(opts2)
        else:
            raise
    _ensure_subtitles(url, result, opts)
    return result
