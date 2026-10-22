"""FastAPI 路由：B 站解析 / 下载任务 / 弹幕 / 评论 / 视频库。

下载任务用后台线程执行（threading），状态与进度写入 SQLite download_tasks 表，
前端轮询 /api/download/tasks 获取进度。
"""
from __future__ import annotations

import logging
import threading
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from . import bilibili, downloader
from .db import DATA_DIR, get_conn, init_db

logger = logging.getLogger(__name__)

DOWNLOAD_DIR = DATA_DIR / "downloads"
ENHANCED_DIR = DATA_DIR / "enhanced"


@asynccontextmanager
async def lifespan(app: FastAPI):
    init_db()
    DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)
    ENHANCED_DIR.mkdir(parents=True, exist_ok=True)
    yield


app = FastAPI(title="本地歌单器后端", version="0.1.0", lifespan=lifespan)


# ============ 基础 ============

@app.get("/health")
def health() -> dict:
    return {"status": "ok", "service": "local-media-player-backend", "version": "0.1.0"}


# ============ B 站解析 / 下载 ============

@app.get("/api/bilibili/parse")
def parse_bilibili(url: str) -> dict:
    """解析 B 站链接：返回标题/UP主/封面/清晰度/分P编号（cid 用于抓弹幕）。"""
    try:
        return downloader.parse_url(url)
    except Exception as e:
        logger.warning("解析失败 %s: %s", url, e)
        raise HTTPException(status_code=400, detail=f"解析失败：{e}")


class DownloadSubmit(BaseModel):
    url: str
    format_sel: str = downloader.DEFAULT_FORMAT


def _extract_bvid(url: str) -> str:
    """从 URL 里本地提取 BV 号（★ 纯字符串，不做网络请求 —— 提交接口不能被网络拖慢）。"""
    import re
    m = re.search(r"(BV[0-9A-Za-z]{10})", url or "")
    return m.group(1) if m else ""


@app.post("/api/download/submit")
def submit_download(body: DownloadSubmit) -> dict:
    """提交下载任务（后台线程执行），返回 task_id。

    ★ 2026-10-17（T15/A2-9）加重复检测，两种情况直接复用已有结果：
      ① 同一 URL 已在队列中(pending/running) → 不再排一个重复任务（返回 queued_already）
      ② 同一 bvid 已经下过、且本地文件仍在 → 不再下一次（返回 duplicate），
         避免"同一文件落两遍 + videos 插两行"（videos 以前没有任何唯一约束，是真隐患）
    """
    url = (body.url or "").strip()
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
        if bvid:
            for r in c.execute(
                "SELECT id, local_path FROM videos WHERE bvid=? ORDER BY id DESC", (bvid,)
            ).fetchall():
                p = (r["local_path"] or "").strip()
                if p and Path(p).exists():
                    logger.info("重复提交（已下载过）bvid=%s → video_id=%s", bvid, r["id"])
                    return {
                        "status": "duplicate",
                        "existing_id": int(r["id"]),
                        "bvid": bvid,
                        "path": p,
                    }
            # 同 bvid 有记录但文件都不在了 → 允许重新下载（不当作重复）
        cur = c.execute(
            "INSERT INTO download_tasks (url, status, updated_at) "
            "VALUES (?, 'pending', datetime('now','localtime'))",
            (url,),
        )
        task_id = int(cur.lastrowid)
    threading.Thread(
        target=_run_download, args=(task_id, url, body.format_sel), daemon=True
    ).start()
    return {"status": "submitted", "task_id": task_id}


def _run_download(task_id: int, url: str, format_sel: str) -> None:
    """★ 2026-10-17（T15）：每次状态/进度变化都刷新 updated_at —— 它是"任务是否还活着"的唯一判据
    （以前只有 created_at，进程被杀后任务永远停在 running，界面永远转圈）。"""

    def progress(p: float) -> None:
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET progress=?, updated_at=datetime('now','localtime') "
                "WHERE id=?",
                (round(p * 100, 1), task_id),
            )

    try:
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='running', "
                "updated_at=datetime('now','localtime') WHERE id=?",
                (task_id,),
            )
        # 先解析拿到 cid（弹幕需要）与元数据
        info = downloader.parse_url(url)
        path = downloader.download(url, DOWNLOAD_DIR, format_sel, progress)
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='done', progress=100, local_path=?, "
                "updated_at=datetime('now','localtime') WHERE id=?",
                (str(path), task_id),
            )
        # 入库 videos 表（供视频库使用）
        _video_to_db(info, path)
        # 尝试抓弹幕入库（cid 拿不到不阻塞；yt-dlp 无 cid 时用 view 接口回退）
        cid = info.get("cid")
        if not cid and info.get("bvid"):
            try:
                cid = bilibili.get_video_info(info["bvid"]).get("cid")
            except Exception as e:
                logger.warning("cid 回退失败: %s", e)
        if cid:
            try:
                danmaku = bilibili.fetch_danmaku(cid)
                _save_danmaku(str(path), danmaku)
            except Exception as e:
                logger.warning("弹幕抓取失败: %s", e)
    except Exception as e:
        logger.warning("下载失败 %s: %s", url, e)
        with get_conn() as c:
            c.execute(
                "UPDATE download_tasks SET status='failed', error=?, reason=?, "
                "updated_at=datetime('now','localtime') WHERE id=?",
                (str(e), "下载失败", task_id),
            )


def _video_to_db(info: dict, path: Path) -> None:
    """★ 2026-10-17（T15）：改成**幂等**写入。
    以前是 `INSERT OR IGNORE` 且不带 quality/page —— 而 videos 表**没有任何唯一约束**，
    那句 IGNORE 是空保险：同一 bvid 再下一次就会插两行、同一文件落两遍。
    现在：同 (bvid, quality, page) 已存在 → 更新那一行；否则插入（含 quality/page）。"""
    bvid = (info.get("bvid") or "").strip()
    quality = (info.get("quality") or "").strip()
    try:
        page = int(info.get("page") or 0)
    except Exception:
        page = 0
    title = info.get("title") or path.stem
    up = info.get("uploader", "")
    desc = info.get("webpage_url", "")
    with get_conn() as c:
        if bvid:
            row = c.execute(
                "SELECT id FROM videos WHERE bvid=? AND quality=? AND page=? ORDER BY id LIMIT 1",
                (bvid, quality, page),
            ).fetchone()
            if row:
                c.execute(
                    "UPDATE videos SET title=?, up_name=?, desc=?, local_path=? WHERE id=?",
                    (title, up, desc, str(path), row["id"]),
                )
                logger.info("videos 已存在同键(bvid=%s quality=%r page=%s) → 更新 id=%s",
                            bvid, quality, page, row["id"])
                return
        c.execute(
            "INSERT INTO videos (bvid, title, up_name, desc, local_path, quality, page) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (bvid, title, up, desc, str(path), quality, page),
        )


# ★ 2026-10-17（T15/A2-9）：僵尸任务自愈 + 失败重试
ZOMBIE_STALE_MIN = 10          # 超过这个分钟数没有进度更新 → 判定为"进程中断"


def sweep_zombie_tasks(stale_min: int = ZOMBIE_STALE_MIN) -> int:
    """把长时间没有进度更新的 pending/running 任务标为 failed。
    来源：下载中被杀进程（本机历史上发生过多次）→ 任务永远停在 running，界面永远转圈。"""
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
        logger.warning("僵尸任务自愈: %d 个 pending/running 超过 %d 分钟无更新 → 标记 failed",
                       n, stale_min)
    return n


@app.post("/api/download/retry")
def retry_download(body: dict) -> dict:
    """重试失败任务：拿原 URL 重新走 submit（复用重复检测，不会同时排两个）。"""
    try:
        tid = int((body or {}).get("task_id") or 0)
    except Exception:
        tid = 0
    if not tid:
        return {"status": "error", "error": "缺少 task_id"}
    with get_conn() as c:
        row = c.execute("SELECT url, status FROM download_tasks WHERE id=?", (tid,)).fetchone()
    if not row:
        return {"status": "error", "error": "任务不存在"}
    if row["status"] in ("pending", "running"):
        return {"status": "queued_already", "task_id": tid}
    return submit_download(DownloadSubmit(url=row["url"]))


def _save_danmaku(local_path: str, danmaku: list[dict]) -> None:
    """整批写弹幕（**先清后写**）。★ 与 services._save_danmaku 同理：
    seg.so 不带弹幕 id、dm_id 为空时老唯一索引约束不到 → 以前每次下载都整批复制一份。"""
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


@app.get("/api/download/tasks")
def list_tasks() -> dict:
    """下载任务列表（最新 50 条，含进度/状态/错误/原因）。

    ★ 2026-10-17（T15）：查询前先做一次**僵尸自愈** —— 进程被杀留下的 pending/running
    会永远转圈，这里顺手把它们标成 failed（幂等、只动超时未更新的行）。"""
    try:
        sweep_zombie_tasks()
    except Exception as e:
        logger.warning("僵尸任务自愈失败（忽略，不影响列表）: %s", e)
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, url, status, progress, local_path, error, reason, "
            "created_at, updated_at "
            "FROM download_tasks ORDER BY id DESC LIMIT 50"
        ).fetchall()
    return {"tasks": [dict(r) for r in rows]}


# ============ 视频库 ============

@app.get("/api/videos")
def list_videos() -> dict:
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, bvid, title, up_name, local_path, created_at "
            "FROM videos ORDER BY id DESC"
        ).fetchall()
    return {"videos": [dict(r) for r in rows]}


@app.get("/api/videos/{vid}/danmaku")
def video_danmaku(vid: int) -> dict:
    """视频弹幕列表（已下载入库的）。"""
    with get_conn() as c:
        rows = c.execute(
            "SELECT time, mode, size, color, text FROM danmaku WHERE video_id=? ORDER BY time",
            (vid,),
        ).fetchall()
    return {"danmaku": [dict(r) for r in rows], "count": len(rows)}


@app.get("/api/videos/{vid}/comments")
def video_comments(vid: int, page: int = 1) -> dict:
    """视频评论：已入库的直接返回；否则按 bvid 抓取并入库。"""
    with get_conn() as c:
        row = c.execute(
            "SELECT bvid FROM videos WHERE id=?", (vid,)
        ).fetchone()
        rows = c.execute(
            "SELECT user_name, content, like_count, created_at FROM comments "
            "WHERE video_id=? ORDER BY like_count DESC LIMIT 100",
            (vid,),
        ).fetchall()
    if rows:
        return {"comments": [dict(r) for r in rows], "cached": True}

    if not row or not row["bvid"]:
        raise HTTPException(status_code=404, detail="视频不存在或无 BV 号")
    try:
        comments = bilibili.fetch_comments(row["bvid"], page=page)
        with get_conn() as c:
            for cm in comments:
                c.execute(
                    "INSERT OR IGNORE INTO comments (video_id, user_name, content, like_count, created_at) "
                    "VALUES (?, ?, ?, ?, ?)",
                    (vid, cm["user_name"], cm["content"], cm["like_count"], str(cm["created_at"])),
                )
        return {"comments": comments, "cached": False}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"评论抓取失败：{e}")


# ============ 增强器 ============

class EnhanceSubmit(BaseModel):
    media_id: int
    media_type: str    # audio（本地歌曲） / video（B站视频）
    enhancer: str      # realesrgan / denoise / loudness / audio_sr
    device: str = "cuda"


@app.post("/api/enhance/submit")
def submit_enhance(body: EnhanceSubmit) -> dict:
    """提交增强任务（后台线程执行），返回 task_id。"""
    with get_conn() as c:
        cur = c.execute(
            "INSERT INTO enhance_tasks (media_id, media_type, enhancer, status) "
            "VALUES (?, ?, ?, 'pending')",
            (body.media_id, body.media_type, body.enhancer),
        )
        task_id = int(cur.lastrowid)
    threading.Thread(
        target=_run_enhance, args=(task_id, body.media_type, body.media_id, body.enhancer, body.device),
        daemon=True,
    ).start()
    return {"task_id": task_id}


def _resolve_media_path(media_type: str, media_id: int) -> str:
    with get_conn() as c:
        if media_type == "audio":
            row = c.execute("SELECT file_path FROM songs WHERE id=?", (media_id,)).fetchone()
        else:
            row = c.execute("SELECT local_path FROM videos WHERE id=?", (media_id,)).fetchone()
    if not row or not row[0]:
        raise ValueError(f"{media_type} #{media_id} 不存在或无文件")
    return row[0]


def _run_enhance(task_id: int, media_type: str, media_id: int, enhancer: str, device: str) -> None:
    from . import ai_pipeline

    def progress(p: float) -> None:
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET progress=? WHERE id=?",
                (round(p * 100, 1), task_id),
            )

    try:
        src = Path(_resolve_media_path(media_type, media_id))
        with get_conn() as c:
            c.execute("UPDATE enhance_tasks SET status='running' WHERE id=?", (task_id,))
        suffix = src.suffix or ".mp4"
        dst = ENHANCED_DIR / f"{enhancer}_{media_type}{media_id}{suffix}"
        ai_pipeline.run_enhance(enhancer, src, dst, device=device, progress_cb=progress)
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET status='done', progress=100, output_path=? WHERE id=?",
                (str(dst), task_id),
            )
    except Exception as e:
        logger.warning("增强失败 task=%s: %s", task_id, e)
        with get_conn() as c:
            c.execute(
                "UPDATE enhance_tasks SET status='failed', error=? WHERE id=?",
                (str(e), task_id),
            )


@app.get("/api/enhance/tasks")
def list_enhance_tasks() -> dict:
    with get_conn() as c:
        rows = c.execute(
            "SELECT id, media_id, media_type, enhancer, status, progress, output_path, error, created_at "
            "FROM enhance_tasks ORDER BY id DESC LIMIT 50"
        ).fetchall()
    return {"tasks": [dict(r) for r in rows]}


@app.get("/api/enhancers")
def enhancers() -> dict:
    """列出已注册的 AI 增强器（模型插槽）"""
    from . import ai_pipeline
    return {"enhancers": ai_pipeline.list_enhancers()}
