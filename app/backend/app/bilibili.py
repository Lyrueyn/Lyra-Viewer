"""B 站弹幕与评论抓取（纯标准库实现，零第三方依赖）。

- 视频信息：view 接口（BV -> aid/cid，无需登录）
- 弹幕：公开 XML 接口 https://comment.bilibili.com/{cid}.xml（deflate 压缩）
- 评论：B 站 wbi 签名（公开算法）+ reply 接口

已验证：BV1GJ411x7h7 → 1200 条弹幕正常解析。
"""
from __future__ import annotations

import base64
import gzip
import hashlib
import json
import logging
import math
import random
import re
import string
import time
import urllib.parse
import urllib.request
import zlib
from pathlib import Path
from typing import Any, Optional
from xml.etree import ElementTree

from . import ffmpeg_tools

logger = logging.getLogger(__name__)

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                  "(KHTML, like Gecko) Chrome/126.0 Safari/537.36",
    "Referer": "https://www.bilibili.com/",
    "Accept-Encoding": "identity",  # 请求明文；CDN 仍可能强制压缩，见 _http_get
}

DANMAKU_URL = "https://comment.bilibili.com/{cid}.xml"

# 登录 Cookie（可选）：从 config.json 加载，用于高清下载（1080p+）
_LOGIN_COOKIE: str = ""


def set_login_cookie(cookie: str) -> None:
    """设置登录 Cookie（如 'SESSDATA=xxx; bili_jct=xxx'），空串清除。"""
    global _LOGIN_COOKIE
    _LOGIN_COOKIE = (cookie or "").strip()


def get_login_cookie() -> str:
    return _LOGIN_COOKIE


def check_login() -> dict:
    """验证当前登录态：调 nav 接口，返回 {is_login, uname, is_vip, vip_type}。"""
    try:
        data = json.loads(_http_get("https://api.bilibili.com/x/web-interface/nav").decode("utf-8"))
        d = data.get("data") or {}
        return {
            "is_login": bool(d.get("isLogin")),
            "uname": d.get("uname") or "",
            "is_vip": bool(d.get("vipStatus")),
            "vip_type": d.get("vipType") or 0,
        }
    except Exception as e:
        return {"is_login": False, "uname": "", "is_vip": False, "vip_type": 0,
                "error": str(e)}


def _headers() -> dict:
    h = dict(HEADERS)
    if _LOGIN_COOKIE:
        # 最终防线：请求头只允许 latin-1（✓ 等脏字符会导致 HTTP 请求失败）
        h["Cookie"] = re.sub(r"[^\x20-\x7E]", "", _LOGIN_COOKIE)
    return h


def _headers_video(bvid: str) -> dict:
    """评论接口专用请求头：模拟浏览器（wbi/main 校验 Referer/Origin/Sec-Fetch）。"""
    h = dict(HEADERS)
    if _LOGIN_COOKIE:
        h["Cookie"] = re.sub(r"[^\x20-\x7E]", "", _LOGIN_COOKIE)
    h["Referer"] = "https://www.bilibili.com/video/{}/".format(bvid)
    h["Origin"] = "https://www.bilibili.com"
    h["Sec-Fetch-Dest"] = "empty"
    h["Sec-Fetch-Mode"] = "cors"
    h["Sec-Fetch-Site"] = "same-site"
    return h


def _http_get(url: str, timeout: int = 15, headers: Optional[dict] = None) -> bytes:
    """GET 并自动解压 gzip / deflate（B 站 CDN 常强制压缩）。"""
    req = urllib.request.Request(url, headers=headers or _headers())
    with urllib.request.urlopen(req, timeout=timeout) as r:
        data = r.read()
        enc = r.headers.get("Content-Encoding", "")
        if enc == "gzip":
            data = gzip.decompress(data)
        elif enc == "deflate":
            try:
                data = zlib.decompress(data)
            except zlib.error:
                data = zlib.decompress(data, -zlib.MAX_WBITS)
        return data


def get_player_subtitles(bvid: str, cid: int) -> list:
    """取视频 CC 字幕列表（/x/player/v2 的 subtitle.subtitles；需要登录 Cookie 才能看到部分字幕）。

    返回 [{"lan", "lan_doc", "url", "ai_status", "ai_type"}]
    """
    try:
        info = get_video_info(bvid)
        aid = info.get("aid") or 0
        url = "https://api.bilibili.com/x/player/v2?aid=%s&cid=%s" % (aid, cid)
        d = json.loads(_http_get(url).decode("utf-8", "replace"))
        subs = ((d.get("data") or {}).get("subtitle") or {}).get("subtitles") or []
        out = []
        for s in subs:
            out.append({
                "lan": s.get("lan") or "",
                "lan_doc": s.get("lan_doc") or "",
                "url": s.get("subtitle_url") or "",
                "ai_status": int(s.get("ai_status") or 0),
                "ai_type": int(s.get("ai_type") or 0),
            })
        return out
    except Exception as e:
        logger.warning("取 B 站字幕列表失败: %s", e)
        return []


def fetch_subtitle_body(subtitle_url: str) -> list:
    """下载 CC 字幕 JSON 并返回 body 列表：[{"from","to","content"}]"""
    try:
        u = subtitle_url or ""
        if u.startswith("//"):
            u = "https:" + u
        if not u:
            return []
        d = json.loads(_http_get(u).decode("utf-8", "replace"))
        return d.get("body") or []
    except Exception as e:
        logger.warning("下载 B 站字幕内容失败: %s", e)
        return []

def get_video_info(bvid: str) -> dict[str, Any]:
    """BV 号 -> {aid, cid, title, owner, duration, pubdate, pages, videos}（view 接口，无需登录）。

    pages 为分P列表（[{cid, page, part, duration}]），多P视频靠它选集下载/播放；
    videos 为分P总数。cid 是第一个分P的 cid。
    """
    url = "https://api.bilibili.com/x/web-interface/view?bvid={}".format(bvid)
    data = json.loads(_http_get(url).decode("utf-8"))
    if data.get("code") != 0:
        raise RuntimeError("视频信息获取失败: {}".format(data.get("message")))
    d = data["data"]
    pages = [
        {"cid": p.get("cid"), "page": p.get("page"), "part": p.get("part"),
         "duration": p.get("duration")}
        for p in (d.get("pages") or [])
    ]
    return {
        "aid": d.get("aid"),
        "cid": d.get("cid"),
        "title": d.get("title", ""),
        "desc": d.get("desc", ""),          # ★ 简介（B站 view 接口本来就有，之前白名单漏掉了）
        "pic": d.get("pic", ""),
        "owner": (d.get("owner") or {}).get("name", ""),
        "duration": d.get("duration", 0),
        "pubdate": d.get("pubdate", 0),          # 视频发布时间（Unix 秒），历史弹幕从这天开始抓
        "pages": pages,
        "videos": d.get("videos", len(pages)),   # 分P总数
        "stat": d.get("stat") or {},             # 播放/弹幕/点赞等统计（view/danmaku/like）
        "pic": d.get("pic") or "",               # ★ 封面图 URL（下载时一并抓下来，首页卡片显示）
    }


def fetch_danmaku(cid: int) -> list[dict[str, Any]]:
    """按 cid 抓取弹幕（分段完整抓取，零依赖手写 protobuf 解析）。

    用分段接口 /x/v2/dm/web/seg.so 抓全部弹幕（每段约 1400 条，
    空段返回 304 即终止）。返回 [{time, mode, size, color, text}]。
    """
    out: list[dict[str, Any]] = []
    seg = 1
    while seg <= 300:
        url = "https://api.bilibili.com/x/v2/dm/web/seg.so?type=1&oid={}&segment_index={}".format(cid, seg)
        try:
            data = _http_get(url, timeout=15)
        except urllib.error.HTTPError as e:
            if e.code == 304:  # 该段无弹幕，抓取结束
                break
            raise
        elems = _parse_seg_protobuf(data)
        if not elems:
            break
        out.extend(elems)
        seg += 1
    return out


def fetch_history_danmaku_dates(cid: int) -> list[str]:
    """按月查询历史弹幕存在的日期列表（需登录 Cookie）。

    返回 ['YYYY-MM-DD', ...]；未登录/无数据时返回空列表。
    从视频发布月（pubdate 所在月）往前推到当前月，减少无效探测。
    """
    dates: list[str] = []
    start_year, start_month = 2015, 1
    now = time.localtime()
    year, month = start_year, start_month
    while (year, month) <= (now.tm_year, now.tm_mon):
        url = "https://api.bilibili.com/x/v2/dm/history/index?type=1&oid={}&month={:04d}-{:02d}".format(cid, year, month)
        try:
            data = json.loads(_http_get(url, timeout=15).decode("utf-8"))
        except Exception:
            month += 1
            if month > 12:
                month, year = 1, year + 1
            continue
        code = data.get("code", 0)
        if code == -101:
            return []   # 未登录，无法用历史接口
        d = data.get("data")
        if isinstance(d, list):
            dates.extend(d)
        month += 1
        if month > 12:
            month, year = 1, year + 1
    return dates


def fetch_history_danmaku(cid: int, date: str) -> list[dict[str, Any]]:
    """拉取某天（YYYY-MM-DD）的历史弹幕（需登录 Cookie，protobuf 同实时段）。

    返回 [{time, mode, size, color, text, id}]；无弹幕/未登录返回空列表。
    """
    url = "https://api.bilibili.com/x/v2/dm/web/history/seg.so?type=1&oid={}&date={}".format(cid, date)
    try:
        data = _http_get(url, timeout=15)
    except urllib.error.HTTPError as e:
        if e.code in (304, 404):
            return []
        raise
    except Exception:
        return []
    return _parse_seg_protobuf(data)


def fetch_all_danmaku(cid: int, fetched_dates: Optional[set[str]] = None) -> list[dict[str, Any]]:
    """抓取某 cid 的完整弹幕：历史接口按天拉全量 + 实时分段补缺，按 dm_id 去重。

    - 有登录 Cookie 且视频有历史弹幕：逐日拉取，弹幕再多也完整（绕开 1200/1400 截断）；
    - fetched_dates：已抓过的日期（断点续传），这些天跳过；
    - 无 Cookie / 无历史数据：退回实时分段抓取（可能有截断）。
    """
    seen: set[int] = set()
    out: list[dict[str, Any]] = []

    def add(elems: list[dict[str, Any]]) -> int:
        added = 0
        for e in elems:
            eid = e.get("id")
            if eid is None:
                continue
            if eid in seen:
                continue
            seen.add(eid)
            out.append(e)
            added += 1
        return added

    if _LOGIN_COOKIE:
        try:
            dates = fetch_history_danmaku_dates(cid)
        except Exception:
            dates = []
        if dates:
            for d in dates:
                if fetched_dates and d in fetched_dates:
                    continue   # 已抓过，跳过（断点续传）
                try:
                    add(fetch_history_danmaku(cid, d))
                except Exception:
                    continue

    # 实时分段补缺（历史接口可能不覆盖最近的弹幕）
    try:
        add(fetch_danmaku(cid))
    except Exception:
        pass

    return out


def fetch_all_danmaku_incremental(cid: int, fetched_dates: Optional[set[str]] = None,
                                  known_dates: Optional[list[str]] = None):
    """增量抓取历史弹幕：逐日 yield (date, elems)，便于每抓一天立即入库+记录进度。

    - 先按天抓历史（跳过 fetched_dates 里已抓的日期）；
    - known_dates：调用方缓存的日期列表（B站对日期接口限流，不能每次现查）；
    - 并发抓取（4 线程）加速：各天独立，中断后从 fetched_dates 之外继续；
    - 最后补实时段（date=None）。
    """
    import concurrent.futures

    if _LOGIN_COOKIE:
        dates = list(known_dates or [])
        # 增量：缓存日期可能过期（B站限流不能每次现查全量），
        # 但至少补查「缓存最新日期之后」到今天的日期，保证新弹幕能被发现。
        last_cached = max(dates) if dates else None
        try:
            fresh = fetch_history_danmaku_dates(cid)
            if fresh:
                if last_cached:
                    # 只取缓存之后的新日期（避免重复请求旧日期）
                    dates = [d for d in dates if d <= last_cached]
                    dates.extend(d for d in fresh if d > last_cached)
                else:
                    dates = fresh
        except Exception:
            pass
        pending = [d for d in dates if not (fetched_dates and d in fetched_dates)]
        if pending:
            def grab(d):
                try:
                    return d, fetch_history_danmaku(cid, d)
                except Exception:
                    return d, []
            with concurrent.futures.ThreadPoolExecutor(max_workers=4) as ex:
                # 按原始日期顺序提交，保持输出有序（进度记录也是有序的）
                for d, elems in ex.map(grab, pending):
                    yield d, elems
        # 历史全部抓完（或已知日期都抓过）→ 不再跑实时补缺（重复且慢）
        if not pending and dates:
            return
    # 实时补缺（仅历史接口不可用/无历史数据时兜底）
    try:
        elems = fetch_danmaku(cid)
    except Exception:
        elems = []
    if elems:
        yield None, elems


# ---- 弹幕 protobuf 手写解析（wire format，零依赖）----

def _parse_varint(buf: bytes, i: int) -> tuple[int, int]:
    result, shift = 0, 0
    while True:
        b = buf[i]
        i += 1
        result |= (b & 0x7F) << shift
        if not (b & 0x80):
            return result, i
        shift += 7


def _parse_dm_elem(data: bytes) -> dict[str, Any]:
    """DanmakuElem: 1=id 2=progress(ms) 3=mode 4=size 5=color 7=content"""
    d: dict[str, Any] = {"id": None, "time": 0.0, "mode": 1, "size": 25, "color": 0xFFFFFF, "text": ""}
    i, n = 0, len(data)
    while i < n:
        tag, i = _parse_varint(data, i)
        field, wire = tag >> 3, tag & 7
        if wire == 0:
            v, i = _parse_varint(data, i)
            if field == 1:
                d["id"] = v
            elif field == 2:
                d["time"] = v / 1000.0
            elif field == 3:
                d["mode"] = v
            elif field == 4:
                d["size"] = v
            elif field == 5:
                d["color"] = v
        elif wire == 2:
            length, i = _parse_varint(data, i)
            s = data[i:i + length]
            i += length
            if field == 7:
                d["text"] = s.decode("utf-8", errors="replace")
        elif wire == 1:
            i += 8
        elif wire == 5:
            i += 4
        else:
            break
    return d


def _parse_seg_protobuf(data: bytes) -> list[dict[str, Any]]:
    """DmSegMobileReply: field 1 = repeated DanmakuElem"""
    elems: list[dict[str, Any]] = []
    i, n = 0, len(data)
    while i < n:
        tag, i = _parse_varint(data, i)
        field, wire = tag >> 3, tag & 7
        if wire == 0:
            _, i = _parse_varint(data, i)
        elif wire == 1:
            i += 8
        elif wire == 2:
            length, i = _parse_varint(data, i)
            sub = data[i:i + length]
            i += length
            if field == 1:
                elems.append(_parse_dm_elem(sub))
        elif wire == 5:
            i += 4
        else:
            break
    return elems


# ============ 评论（wbi 签名，公开算法）============

MIXIN_KEY_ENC_TAB = [
    46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
    33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
    61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
    36, 20, 34, 44, 52,
]


def _mixin_key(orig: str) -> str:
    return "".join(orig[i] for i in MIXIN_KEY_ENC_TAB[:32])


def _wbi_keys() -> tuple[str, str]:
    data = json.loads(_http_get("https://api.bilibili.com/x/web-interface/nav").decode("utf-8"))
    img = data["data"]["wbi_img"]
    img_key = img["img_url"].rsplit("/", 1)[1].split(".")[0]
    sub_key = img["sub_url"].rsplit("/", 1)[1].split(".")[0]
    return img_key, sub_key


def _wbi_sign(params: dict, img_key: str, sub_key: str) -> dict:
    mixin = _mixin_key(img_key + sub_key)
    p = {k: str(v) for k, v in params.items()}
    p["wts"] = str(int(time.time()))
    query = urllib.parse.urlencode(sorted(p.items()))
    p["w_rid"] = hashlib.md5((query + mixin).encode()).hexdigest()
    return p


def fetch_comments(bvid: str, page: int = 1, page_size: int = 20,
                   sort: int = 2) -> list[dict[str, Any]]:
    """抓取视频评论（sort=2 热评 / sort=1 时间），返回 [{user_name, content, like_count, created_at, reply_count}]。"""
    info = get_video_info(bvid)
    img_key, sub_key = _wbi_keys()
    params = _wbi_sign(
        {"type": "1", "oid": info["aid"], "sort": str(sort), "pn": page, "ps": page_size},
        img_key, sub_key,
    )
    url = "https://api.bilibili.com/x/v2/reply?" + urllib.parse.urlencode(params)
    data = json.loads(_http_get(url).decode("utf-8"))
    if data.get("code") != 0:
        raise RuntimeError("评论获取失败: {}".format(data.get("message")))

    out: list[dict[str, Any]] = []
    for c in (data.get("data") or {}).get("replies") or []:
        top_rpid = c.get("rpid")
        item = {
            "rpid": top_rpid,
            "user_name": (c.get("member") or {}).get("uname", ""),
            "content": (c.get("content") or {}).get("message", ""),
            "like_count": c.get("like", 0),
            "created_at": c.get("ctime", 0),
            "reply_count": c.get("rcount", 0),
        }
        # ★ B站主接口会在每条评论里带前 2~3 条"楼中楼"预览 —— 以前整个丢掉了，
        #   所以库里几乎没有回复数据。这里一并解析出来（replies 字段）。
        subs = []
        for s in c.get("replies") or []:
            subs.append({
                "rpid": s.get("rpid"),
                "user_name": (s.get("member") or {}).get("uname", ""),
                "content": (s.get("content") or {}).get("message", ""),
                "like_count": s.get("like", 0),
                "created_at": s.get("ctime", 0),
                "parent": s.get("parent") or top_rpid,
                "root": s.get("root") or top_rpid,
            })
        item["replies"] = subs
        out.append(item)
    return out


def fetch_all_comments(bvid: str, max_pages: int = 500, on_page=None,
                       since_ts: int = 0) -> list[dict[str, Any]]:
    """抓取全部顶层评论（旧接口 pn 翻页，稳定可抓全）。

    - 主路径 sort=1（时间排序 新→旧）：since_ts>0 时按时间增量抓取，
      遇到 created_at <= since_ts 的评论即停止（时间排序下后面更早，可提前截断）；
    - 再补 sort=2（热度）并集兜底；
    - on_page(replies) 可选：每页回调（增量入库/进度）。
    """
    out: list[dict[str, Any]] = []
    seen: set[int] = set()

    def grab(sort: int, time_cut: bool = False) -> int:
        """旧接口按 sort 排序翻页抓取，返回新增条数。"""
        added_total = 0
        for pn in range(1, max_pages + 1):
            try:
                page = fetch_comments(bvid, page=pn, page_size=20, sort=sort)
            except RuntimeError:
                return added_total
            if not page:
                return added_total
            page_elems = []
            for c in page:
                if time_cut and since_ts > 0 and (c.get("created_at") or 0) <= since_ts:
                    return added_total   # 时间排序：遇到更早的即已抓过，提前停止
                if c.get("rpid") and c["rpid"] in seen:
                    continue
                if c.get("rpid"):
                    seen.add(c["rpid"])
                page_elems.append(c)
            if not page_elems:
                return added_total   # 本页全重复 = 抓完
            out.extend(page_elems)
            added_total += len(page_elems)
            if on_page:
                on_page(page_elems)
            time.sleep(0.1)
        return added_total

    # 主路径：先按时间排序增量抓（可截断），再补热度并集
    grab(1, time_cut=True)
    if not (since_ts > 0):
        grab(2)   # 全量时补热度覆盖；增量时跳过（热度会重复已抓的）
    if out:
        return out

    # 兜底：wbi/main（新接口，若旧接口失败）
    if _LOGIN_COOKIE:
        try:
            return _fetch_comments_wbi_main(bvid, max_pages, on_page)
        except Exception:
            pass
    return out


def _fetch_comments_wbi_main(bvid: str, max_pages: int = 500,
                             on_page=None) -> list[dict[str, Any]]:
    """新版评论接口：x/v2/reply/wbi/main 游标分页抓全量顶层评论（需登录 cookie）。

    策略：B站对单排序游标翻页有限制（约 64 页后被 -403 截断），
    用多个排序（2=时间, 1=热度）各抓一遍取并集，尽量补全。
    on_page(replies) 可选：每抓一页调用一次（调用方可立即入库/更新进度）。
    """
    out: list[dict[str, Any]] = []
    seen: set[int] = set()

    def grab(mode: int) -> int:
        """抓一个排序，返回新增条数。遇 -403（页数上限/风控）换下一个排序。"""
        added_total = 0
        try:
            img_key, sub_key = _wbi_keys()
        except Exception:
            return 0
        offset = ""
        for _ in range(max_pages):
            pagination = json.dumps({"offset": offset}, ensure_ascii=False)
            params = _wbi_sign({
                "oid": str(_oid), "type": "1", "mode": str(mode),
                "pagination_str": pagination,
                "plat": "1", "seek_rpid": "", "web_location": "1315875",
            }, img_key, sub_key)
            url = "https://api.bilibili.com/x/v2/reply/wbi/main?" + urllib.parse.urlencode(params)
            data = json.loads(_http_get(url, timeout=15, headers=_headers_video(bvid)).decode("utf-8"))
            if data.get("code") != 0:
                # -403 = 翻页上限/风控：本排序结束，换下一个排序
                return added_total
            d = data.get("data") or {}
            replies = d.get("replies") or []
            if not replies:
                return added_total   # 空页 = 该排序抓完
            page_elems: list[dict[str, Any]] = []
            for c in replies:
                rpid = c.get("rpid")
                if rpid and rpid in seen:
                    continue
                if rpid:
                    seen.add(rpid)
                elem = {
                    "rpid": rpid,
                    "user_name": (c.get("member") or {}).get("uname", ""),
                    "content": (c.get("content") or {}).get("message", ""),
                    "like_count": c.get("like", 0),
                    "created_at": c.get("ctime", 0),
                    "reply_count": c.get("rcount", 0),
                }
                page_elems.append(elem)
            if page_elems:
                out.extend(page_elems)
                added_total += len(page_elems)
                if on_page:
                    on_page(page_elems)
            next_off = ((d.get("cursor") or {}).get("pagination_reply") or {}).get("next_offset")
            if not next_off or next_off == offset:
                return added_total
            offset = next_off
            time.sleep(0.12)
        return added_total

    info = get_video_info(bvid)
    _oid = info["aid"]
    # 时间排序优先（能到页数上限），再补热度排序（覆盖更早的评论）
    grab(2)
    grab(1)
    return out


def fetch_comment_replies(bvid: str, root_rpid: int, page: int = 1,
                          page_size: int = 20) -> list[dict[str, Any]]:
    """抓取某条评论的楼中楼子评论（x/v2/reply/reply 接口）。

    返回 [{rpid, user_name, content, like_count, created_at, parent}]，
    parent = 被回复的评论 rpid（多数是顶层 rpid）。
    """
    info = get_video_info(bvid)
    img_key, sub_key = _wbi_keys()
    params = _wbi_sign(
        {"type": "1", "oid": info["aid"], "root": root_rpid, "pn": page, "ps": page_size},
        img_key, sub_key,
    )
    url = "https://api.bilibili.com/x/v2/reply/reply?" + urllib.parse.urlencode(params)
    data = json.loads(_http_get(url).decode("utf-8"))
    if data.get("code") != 0:
        raise RuntimeError("子评论获取失败: {}".format(data.get("message")))

    out: list[dict[str, Any]] = []
    for c in (data.get("data") or {}).get("replies") or []:
        out.append({
            "rpid": c.get("rpid"),
            "user_name": (c.get("member") or {}).get("uname", ""),
            "content": (c.get("content") or {}).get("message", ""),
            "like_count": c.get("like", 0),
            "created_at": c.get("ctime", 0),
            "parent": c.get("parent") or root_rpid,
            "root": c.get("root") or root_rpid,
        })
    return out


# ============ 视频下载（纯标准库，免登录 480p）============

def _wbi_url(api: str, params: dict) -> str:
    img_key, sub_key = _wbi_keys()
    signed = _wbi_sign(params, img_key, sub_key)
    return api + "?" + urllib.parse.urlencode(signed)


def _dm_params() -> dict:
    """B 站风控参数（模拟播放器环境，获取高清流必需，算法同 yt-dlp）。"""
    def get_wh(width=1920, height=1080):
        rnd = math.floor(114 * random.random())
        return [2 * width + 2 * height + 3 * rnd, 4 * width - height + rnd, rnd]

    def get_of(scroll_top=10, scroll_left=10):
        rnd = math.floor(514 * random.random())
        return [3 * scroll_top + 2 * scroll_left + rnd,
                4 * scroll_top - 4 * scroll_left + 2 * rnd, rnd]

    return {
        "dm_img_list": "[]",
        "dm_img_str": base64.b64encode(
            "".join(random.choices(string.printable, k=random.randint(16, 64))).encode())[:-2].decode(),
        "dm_cover_img_str": base64.b64encode(
            "".join(random.choices(string.printable, k=random.randint(32, 128))).encode())[:-2].decode(),
        "dm_img_inter": json.dumps({
            "ds": [],
            "wh": get_wh(1920, 1080),
            "of": get_of(random.randint(0, 100), 0),
        }, separators=(",", ":")),
    }


QUALITY_NAMES = {
    125: "杜比视界", 120: "4K", 116: "1080P60", 112: "1080P+",
    80: "1080P", 64: "720P", 32: "480P", 16: "360P",
}

# 清晰度档位 → 最大高度（选择流时限制，不超目标画质）
_QN_HEIGHT = {
    125: 2160, 120: 2160, 116: 1080, 112: 2160, 80: 1080,
    64: 720, 32: 480, 16: 360,
}


def _pick_video_stream(videos: list, max_height: int, want_qn: int = 0) -> Optional[dict]:
    """从 DASH 视频流里选一路。

    ★ 2026-10-14 修两处：
    ① **优先选与请求档位 qn 完全一致的那路**：B站同一高度常有多路
       （实测这条 HDR 片：id=125 杜比视界、id=120 4K 都是 2160），只比 height 会平手取到
       杜比视界那路 → "请求 4K"实际下的是杜比视界（还被标成 4K）。
    ② **不再"兜底取最低流"**：以前没有 ≤ 目标高度的流时会退回 `min(videos)`，
       一旦列表里只有更高的流（例如只给 2160 那路），就会出现"请求 720P 却下回来 4K"的情况。
       现在返回 None，由调用方换下一档或报错，绝不偷偷升级。
    """
    if want_qn:
        for v in videos:
            if v.get("id") == want_qn:
                return v
    candidates = [v for v in videos if (v.get("height") or 0) <= max_height]
    if candidates:
        return max(candidates, key=lambda x: x.get("height") or 0)
    return None


def get_available_qualities(bvid: str, cid: int) -> list[dict]:
    """返回该视频可用的清晰度档位（按 qn 去重，每档一项），按档位降序。

    注意：不能按 height 去重——杜比视界(125)与普通4K(120)高度都是 2160，
    按高度去重会丢失 4K 档。
    """
    params = {"bvid": bvid, "cid": cid, "qn": 120, "fnval": 4048, "fourk": 1}
    params.update(_dm_params())
    url = _wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
    data = json.loads(_http_get(url).decode("utf-8"))
    if data.get("code") != 0:
        raise RuntimeError("获取清晰度列表失败: {}".format(data.get("message")))
    dash = (data.get("data") or {}).get("dash") or {}
    # ★ B站官方档位描述（support_formats）：qn=125 就是 "HDR 真彩"，4K/1080P 等都是普通档。
    #   带进界面，用户才知道**哪一档才是 HDR**（实测这条片子里只有 125 是 10bit PQ HDR10，
    #   4K(120) 虽然 3840×2160 但只是 h264 8bit SDR）。
    sup = {}
    for f in (data.get("data") or {}).get("support_formats") or []:
        try:
            sup[int(f.get("quality") or 0)] = (f.get("new_description") or "").strip()
        except (TypeError, ValueError):
            pass
    seen: dict[int, dict] = {}
    for v in dash.get("video") or []:
        q = v.get("id")
        h = v.get("height")
        if q and h and q not in seen:
            note = QUALITY_NAMES.get(q, "{}P".format(h))
            desc = sup.get(q, "")
            if desc and "hdr" in desc.lower():
                note += "（HDR）"          # 让界面明确标出 HDR 档（B站官方描述 "HDR 真彩"）
            seen[q] = {"qn": q, "height": h,
                       "note": note,
                       "codecid": v.get("codecid"),
                       "codecs": v.get("codecs")}
    return sorted(seen.values(), key=lambda x: -x["qn"])


def get_playurl(bvid: str, cid: int, qn: int = 120, hi_res: bool = True) -> dict[str, Any]:
    """获取播放流（DASH：视频+音频分离）。

    qn: 32=480p，64=720p，80=1080p，112=1080p+，116=1080p60，120=4K。
    带登录 Cookie + 风控参数，可拿大会员高清；权限不足自动降级。
    hi_res=True 时若 B站提供 Hi-Res FLAC 无损音轨则优先（返回 audio_codec=flac/hi_res=True），
    hi_res=False 强制用标准 AAC 音轨（文件更小）。
    返回 {"title", "video_url", "audio_url", "quality", "height", "hi_res", "audio_codec"}
    """
    best: Optional[dict[str, Any]] = None
    # 必须用新版 wbi/playurl + fnval=4048（4K/HDR）+ dm 风控参数
    for q in (qn, 112, 80, 64, 32):
        params = {"bvid": bvid, "cid": cid, "qn": q, "fnval": 4048, "fourk": 1}
        params.update(_dm_params())
        url = _wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
        try:
            data = json.loads(_http_get(url).decode("utf-8"))
        except Exception:
            continue
        if data.get("code") != 0:
            continue
        d = data.get("data") or {}
        dash = d.get("dash") or {}
        videos = dash.get("video") or []
        audios = dash.get("audio") or []
        if not videos or not audios:
            continue
        # 选一路流：优先与请求档位 qn 完全一致的那路；否则取不高于目标高度的最高流（降级）。
        # 取不到（列表里只有更高的流）就换下一档 —— 绝不"兜底升到更高画质"。
        v = _pick_video_stream(videos, _QN_HEIGHT.get(q, 2160), want_qn=q)
        if not v:
            continue
        # ★ 优先 Hi-Res FLAC 音轨（B站 dash.flac 为 24/96 等真无损），没有才用 AAC；hi_res=False 则强制 AAC
        audio_url = ""
        hi_res_found = False
        if hi_res:
            flac = dash.get("flac")
            if isinstance(flac, dict):
                fa = flac.get("audio")
                if isinstance(fa, dict):
                    audio_url = fa.get("baseUrl") or fa.get("base_url") or ""
                    if audio_url:
                        hi_res_found = True
        if not audio_url:
            audio_url = audios[0].get("baseUrl") or audios[0].get("base_url") or ""
        cand = {
            "title": d.get("title", bvid),
            "video_url": v.get("baseUrl") or v.get("base_url") or "",
            "audio_url": audio_url,
            # ★ 用**实际选中那路流的 id** 作为画质（而不是 API 的 data.quality）：
            #   否则会出现"文件名/入库标 4K、内容其实是杜比视界"这种标注与实际不符。
            "quality": v.get("id") or d.get("quality", q),
            "height": v.get("height"),
            "hi_res": hi_res_found,
            "audio_codec": "flac" if hi_res_found else "aac",
        }
        if cand["quality"] >= q:
            return cand       # 达到目标清晰度
        if best is None or cand["quality"] > best["quality"]:
            best = cand       # 记录最高可用（降级用）
    if best:
        return best
    raise RuntimeError("该视频无可用 DASH 流（可能需登录或视频受限）")


def _download_url(url: str, dest: Path, progress_cb=None, chunk: int = 1024 * 256) -> None:
    """按 Range 分片下载（B 站流媒体防盗链需要 Referer/UA/Cookie）。

    progress_cb 每块都会被调用：既能上报进度，也能抛异常中断（取消检查）。
    无 Content-Length（chunked）时以近似值上报，保证进度回调仍被高频触发。
    """
    headers = dict(_headers())
    headers["Range"] = "bytes=0-"
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=30) as r, open(dest, "wb") as f:
        total = int(r.headers.get("Content-Length") or 0) or None
        done = 0
        while True:
            b = r.read(chunk)
            if not b:
                break
            f.write(b)
            done += len(b)
            if progress_cb:
                # 有 total 用真实比例；无 total 用"已下载字节数"作为单调递增值，仅用于取消检查触发
                progress_cb(done / total if total else done / (done + 1))


def download_video(bvid: str, cid: int, out_dir: Path,
                   progress_cb=None, title: Optional[str] = None,
                   qn: int = 120, hi_res: bool = True,
                   page: int = 0) -> tuple[Path, dict]:
    """零依赖下载 B 站视频（带登录 Cookie 可下 4K/高帧率）→ mp4，返回 (文件路径, 信息)。

    流程：playurl 拿流地址 → 分别下载视频/音频 → ffmpeg 合并。
    qn 指定目标清晰度（120=4K，80=1080P…），权限不足自动降级。
    hi_res=True 时若 B站提供 Hi-Res FLAC 音轨则优先（文件较大，无损）；False 用 AAC（较小）。
    page 为分P序号(1-based，0=单视频/第一P)，多P时文件名带 [P{n}] 区分，避免互相覆盖。
    文件名带清晰度标记（如《流光》[4K].mp4），不同画质可共存。
    """
    import subprocess
    import tempfile

    stream = get_playurl(bvid, cid, qn=qn, hi_res=hi_res)
    ffmpeg = _find_ffmpeg()
    if not ffmpeg:
        raise RuntimeError("未找到 ffmpeg（合并音视频需要）。" + ffmpeg_tools.HINT)

    out_dir.mkdir(parents=True, exist_ok=True)
    tmp = Path(tempfile.mkdtemp(prefix="bili_"))
    try:
        vfile = tmp / "video.m4s"
        afile = tmp / "audio.m4s"

        def half(p):
            if progress_cb:
                progress_cb(p * 0.9)

        _download_url(stream["video_url"], vfile, half)
        _download_url(stream["audio_url"], afile, half)

        # 文件名：标题 + [分P]（多P区分，避免覆盖）+ [清晰度]（不同画质共存）+ Hi-Res 标记（若音频为 FLAC 无损）
        # 必须清掉 Windows 非法字符（| : * ? " < > 等）；B站标题常含 | 会致 ffmpeg 打开输出文件失败
        file_title = re.sub(r'[\\/:*?"<>|]', '_', (title or stream.get("title") or bvid)).strip()
        quality_note = QUALITY_NAMES.get(stream["quality"],
                                         "{}P".format(stream["height"] or ""))
        hires_note = " Hi-Res" if stream.get("hi_res") else ""
        page_note = "[P{}]".format(page) if page > 0 else ""
        dst = out_dir / "{}{}{} [{}].mp4".format(file_title, page_note, hires_note, quality_note)
        # ffmpeg 合并（把真实 stderr 带进异常，失败时自动兜底换一种合并方式）
        # mp4 容器支持 FLAC（ffmpeg 7.x 实测 -c copy 成功），故 Hi-Res FLAC 音轨可无损存进 mp4
        def _merge(extra: list[str]) -> subprocess.CompletedProcess:
            cmd = [ffmpeg, "-y", "-i", str(vfile), "-i", str(afile),
                   "-c", "copy"] + extra + [str(dst)]
            return subprocess.run(cmd, capture_output=True, text=True)

        r = _merge(["-movflags", "+faststart"])
        if r.returncode != 0:
            # 兜底：去掉 +faststart（个别环境它写 moov 时因文件名/容器报 Invalid argument）
            r2 = _merge([])
            if r2.returncode != 0:
                detail = (r.stderr or r.stdout or "").strip()[-900:]
                detail2 = (r2.stderr or r2.stdout or "").strip()[-400:]
                raise RuntimeError(
                    "ffmpeg 合并失败：{} | 兜底(无faststart)也失败：{}".format(detail, detail2))
        if progress_cb:
            progress_cb(1.0)
        return dst, stream
    finally:
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)


def _find_ffmpeg() -> Optional[str]:
    """统一解析（T24：支持 FFMPEG_PATH；详见 app/ffmpeg_tools.py）。找不到返回 None。"""
    return ffmpeg_tools.find_ffmpeg(required=False) or None
