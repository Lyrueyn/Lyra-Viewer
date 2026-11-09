"""诊断：为什么历史弹幕没抓到（进度缓存 / 登录 cookie / 日期列表接口）。"""
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
vid = 60

c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
c.row_factory = sqlite3.Row
print("=== danmaku_progress（video 60）===")
for r in c.execute("SELECT * FROM danmaku_progress WHERE video_id=?", (vid,)):
    d = dict(r)
    fd = d.get("fetched_dates") or ""
    print("  cid=%s 已抓日期数=%s updated=%s" % (d.get("cid"), len(fd.split(",")), d.get("updated_at")))
    print("  fetched_dates=%s" % fd[:300])
print()

ck = services.get_cookie()
print("=== 登录 cookie ===")
print("  长度=%s 含SESSDATA=%s" % (len(ck or ""), "SESSDATA" in (ck or "")))
bilibili.set_login_cookie(ck)

with services.get_conn() as cc:
    bvid = cc.execute("SELECT bvid FROM videos WHERE id=?", (vid,)).fetchone()["bvid"]
print("=== 视频信息 ===")
info = bilibili.get_video_info(bvid)
cid = info["cid"]
print("  bvid=%s cid=%s 播放=%s B站弹幕总数=%s"
      % (bvid, cid, (info.get("stat") or {}).get("view"), (info.get("stat") or {}).get("danmaku")))
print()
print("=== 历史日期列表接口 ===")
try:
    dates = bilibili.fetch_history_danmaku_dates(cid)
    print("  返回 %s 个日期: %s" % (len(dates), dates[:12]))
except Exception as e:
    print("  失败: %s: %s" % (type(e).__name__, e))
print()
print("=== 实时池接口（seg.so）===")
try:
    live = bilibili.fetch_danmaku(cid)
    print("  返回 %s 条" % len(live))
except Exception as e:
    print("  失败: %s: %s" % (type(e).__name__, e))
print()
print("=== 单日历史接口试抓 ===")
if dates:
    d0 = dates[0]
    try:
        h = bilibili.fetch_history_danmaku(cid, d0)
        print("  %s → %s 条" % (d0, len(h)))
    except Exception as e:
        print("  失败: %s: %s" % (type(e).__name__, e))
