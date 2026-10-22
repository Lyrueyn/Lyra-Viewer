"""后端自检脚本：零依赖一键回归（不依赖任何第三方包）。

验证：数据库 / 视频信息 / 弹幕 / 评论 / 播放流 / 增强器注册。
用法：py -3.10 selfcheck.py
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from app import ai_pipeline, bilibili, services  # noqa: E402
from app.db import get_conn, init_db  # noqa: E402

BVID = "BV1GJ411x7h7"  # 测试用公开视频（Never Gonna Give You Up 官方 MV）
PASS = 0
FAIL = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global PASS, FAIL
    if ok:
        PASS += 1
        print("  [PASS] {}".format(name))
    else:
        FAIL += 1
        print("  [FAIL] {} {}".format(name, detail))


def main() -> int:
    print("== 本地歌单器后端自检 ==")

    print("-- 数据库 --")
    init_db()
    with get_conn() as c:
        tables = {r[0] for r in c.execute(
            "SELECT name FROM sqlite_master WHERE type='table'").fetchall()}
    need = {"songs", "playlists", "playlist_items", "videos", "danmaku",
            "comments", "download_tasks", "enhance_tasks", "history"}
    check("表齐全", need <= tables, "缺: {}".format(need - tables))

    print("-- 增强器注册 --")
    enhancers = ai_pipeline.list_enhancers()
    check("4 个增强器", len(enhancers) == 4, str(enhancers))

    print("-- B 站接口（真实网络）--")
    try:
        info = bilibili.get_video_info(BVID)
        check("视频信息", bool(info.get("cid")), str(info))
        cid = info["cid"]
    except Exception as e:
        check("视频信息", False, str(e))
        cid = None

    if cid:
        try:
            dm = bilibili.fetch_danmaku(cid)
            check("弹幕抓取", len(dm) > 0, "{} 条".format(len(dm)))
        except Exception as e:
            check("弹幕抓取", False, str(e))
        try:
            cm = bilibili.fetch_comments(BVID, page=1, page_size=5)
            check("评论抓取", len(cm) > 0, "{} 条".format(len(cm)))
        except Exception as e:
            check("评论抓取", False, str(e))
        try:
            stream = bilibili.get_playurl(BVID, cid)
            check("播放流解析", bool(stream.get("video_url")) and bool(stream.get("audio_url")))
        except Exception as e:
            check("播放流解析", False, str(e))

    print("-- 服务层 --")
    check("健康检查", services.health()["status"] == "ok")
    try:
        videos = services.list_videos()
        check("视频库查询", "videos" in videos)
    except Exception as e:
        check("视频库查询", False, str(e))

    print("=" * 30)
    print("结果: {} 通过 / {} 失败".format(PASS, FAIL))
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
