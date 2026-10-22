"""合集/歌单管理（本地 SQLite playlists / playlist_items，歌曲+视频混排）。

单独一个模块，避免改动庞大的 services.py；由 stdlib_server 的 playlists 路由调用。
表结构与 db.py / C++ MediaLibrary 一致。
"""
from __future__ import annotations

from .db import get_conn


def list_playlists() -> dict:
    """全部歌单（含视频项计数）。"""
    with get_conn() as c:
        rows = c.execute(
            "SELECT p.id, p.name, "
            "  (SELECT COUNT(*) FROM playlist_items pi WHERE pi.playlist_id=p.id) AS cnt "
            "FROM playlists p ORDER BY p.id DESC"
        ).fetchall()
    return {"playlists": [{"id": r["id"], "name": r["name"], "count": r["cnt"]} for r in rows]}


def playlist_items(playlist_id: int) -> dict:
    """歌单内的视频/歌曲项（JOIN videos/songs 拿标题/路径/封面）。

    ★ 2026-10-14：补上 cover_path / duration / quality / page —— 合集页也要像首页一样显示封面卡片。
    """
    with get_conn() as c:
        name_row = c.execute("SELECT name FROM playlists WHERE id=?", (playlist_id,)).fetchone()
        v = c.execute(
            "SELECT pi.media_type, pi.media_id, pi.position, "
            "       v.title, v.up_name, v.local_path, v.bvid, "
            "       v.cover_path, v.duration, v.quality, v.page "
            "FROM playlist_items pi LEFT JOIN videos v ON v.id=pi.media_id "
            "WHERE pi.playlist_id=? AND pi.media_type='video' "
            "ORDER BY pi.position",
            (playlist_id,),
        ).fetchall()
    items = [{
        "media_type": r["media_type"], "media_id": r["media_id"], "position": r["position"],
        "title": r["title"] or "", "up_name": r["up_name"] or "",
        "local_path": r["local_path"] or "", "bvid": r["bvid"] or "",
        "cover_path": r["cover_path"] or "", "duration": r["duration"] or 0,
        "quality": r["quality"] or "", "page": r["page"] or 0,
    } for r in v]
    return {"playlist_id": playlist_id, "id": playlist_id,
            "name": (name_row["name"] if name_row else "") or "",   # ★ 视频窗口要显示"来源合集"的名字
            "items": items, "count": len(items)}


def create_playlist(name: str) -> dict:
    name = (name or "").strip()
    if not name:
        raise ValueError("合集名不能为空")
    with get_conn() as c:
        cur = c.execute("INSERT INTO playlists (name) VALUES (?)", (name,))
        return {"playlist_id": int(cur.lastrowid), "name": name}


def add_video_to_playlist(playlist_id: int, video_id: int) -> dict:
    """把视频加入合集。★ 校验 video_id 有效，且**不允许重复加入**（以前会插入 media_id=0 的孤儿条目/重复条目）。"""
    if video_id <= 0:
        return {"ok": False, "error": "video_id 无效（需要库内视频记录）"}
    with get_conn() as c:
        row = c.execute("SELECT 1 FROM videos WHERE id=?", (video_id,)).fetchone()
        if not row:
            return {"ok": False, "error": "视频不在库中（video_id=%s）" % video_id}
        dup = c.execute(
            "SELECT 1 FROM playlist_items WHERE playlist_id=? AND media_type='video' AND media_id=?",
            (playlist_id, video_id),
        ).fetchone()
        if dup:
            return {"ok": True, "duplicate": True}
        n = c.execute(
            "SELECT COALESCE(MAX(position),0)+1 FROM playlist_items WHERE playlist_id=?",
            (playlist_id,),
        ).fetchone()[0]
        c.execute(
            "INSERT INTO playlist_items (playlist_id, media_type, media_id, position) "
            "VALUES (?, 'video', ?, ?)",
            (playlist_id, video_id, n),
        )
    return {"ok": True, "added": True}


def remove_video_from_playlist(playlist_id: int, video_id: int) -> dict:
    with get_conn() as c:
        c.execute(
            "DELETE FROM playlist_items WHERE playlist_id=? AND media_type='video' AND media_id=?",
            (playlist_id, video_id),
        )
    return {"ok": True}


def delete_playlist(playlist_id: int) -> dict:
    with get_conn() as c:
        c.execute("DELETE FROM playlist_items WHERE playlist_id=?", (playlist_id,))
        c.execute("DELETE FROM playlists WHERE id=?", (playlist_id,))
    return {"ok": True}
