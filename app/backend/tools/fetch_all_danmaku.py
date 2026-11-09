"""补全某个视频的全部弹幕（重置进度缓存后按日期翻历史，最多 425 个日期）。

背景：B站历史弹幕接口每个日期最多返回 500 条、各日期返回不同子集，并集才是全量；
而 danmaku_progress 里被错误标记过"已抓"（当时没登录，空结果也记了进度）→ 增量会全跳过。
本脚本先清掉该视频的进度缓存，再跑完整刷新，最后再跑一次验证幂等（不会翻倍）。

用法：cd backend && .venv\\Scripts\\python.exe tools\\fetch_all_danmaku.py 60
"""
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import services  # noqa: E402

DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
vid = int(sys.argv[1]) if len(sys.argv) > 1 else 60


def counts(tag: str) -> None:
    c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
    row = c.execute(
        "SELECT COUNT(*), SUM(CASE WHEN dm_id IS NULL THEN 1 ELSE 0 END), "
        "(SELECT COUNT(*) FROM (SELECT DISTINCT time,mode,size,color,text FROM danmaku WHERE video_id=?)) "
        "FROM danmaku WHERE video_id=?", (vid, vid)
    ).fetchone()
    print("[%s] 行数=%s 无id=%s 内容去重=%s" % (tag, row[0], row[1], row[2]), flush=True)
    c.close()


with services.get_conn() as c:
    n = c.execute("DELETE FROM danmaku_progress WHERE video_id=?", (vid,)).rowcount
print("已清掉进度缓存（%s 行）→ 下次刷新会重新翻所有日期" % n, flush=True)

counts("开始前")
t0 = time.time()
res = services.refresh_video(vid)
print("刷新完成 → %s（耗时 %.0f 秒）" % (res, time.time() - t0), flush=True)
counts("刷新后")

t1 = time.time()
res2 = services.refresh_video(vid)
print("再刷一次 → %s（耗时 %.0f 秒；新增应为 0，验证不会重复）" % (res2, time.time() - t1), flush=True)
counts("二次刷新后")
