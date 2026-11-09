"""手动跑一次 video 60 的完整弹幕刷新（含按日期翻历史），用于补全 + 验证不重复。

用法（必须在 backend 目录、用后端自己的 venv python）：
  cd D:\\DSH\\local-media-player\\backend
  .venv\\Scripts\\python.exe tools\\refresh_one.py 60
"""
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import services  # noqa: E402

vid = int(sys.argv[1]) if len(sys.argv) > 1 else 60
DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"


def counts(tag: str) -> None:
    c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
    total, noid, uniq = c.execute(
        "SELECT COUNT(*), SUM(CASE WHEN dm_id IS NULL THEN 1 ELSE 0 END), "
        "(SELECT COUNT(*) FROM (SELECT DISTINCT time,mode,size,color,text FROM danmaku "
        " WHERE video_id=?)) FROM danmaku WHERE video_id=?", (vid, vid)
    ).fetchone()
    print("[%s] 行数=%s 无id=%s 去重=%s" % (tag, total, noid, uniq), flush=True)
    c.close()


counts("刷新前")
t0 = time.time()
res = services.refresh_video(vid)
print("refresh_video → %s（耗时 %.1f 秒）" % (res, time.time() - t0), flush=True)
counts("刷新后")
print("再刷一次（验证幂等，不会翻倍）", flush=True)
res2 = services.refresh_video(vid)
print("refresh_video #2 → %s" % (res2,), flush=True)
counts("二次刷新后")
