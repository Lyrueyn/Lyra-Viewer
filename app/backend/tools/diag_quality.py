"""诊断下载画质：同一视频请求不同 qn，看 get_playurl 实际返回哪一路流。

如果请求 720p 却返回 quality=120(4K)，就会导致：
  - 文件名/入库画质都写成 4K → _video_to_db 命中已有那条 4K 记录 → 覆盖同一个文件
  → 用户视角"下了一个画质就不能下另一个画质"。
"""
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

bilibili.set_login_cookie(services.get_cookie())
DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"

c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
c.row_factory = sqlite3.Row
print("=== 库里已有的记录（视频 58 / 60）===")
for r in c.execute("SELECT id, bvid, quality, page, local_path FROM videos WHERE id IN (58,60)"):
    print("  id=%s bvid=%s quality=%r page=%s" % (r["id"], r["bvid"], r["quality"], r["page"]))
print("  —— 按 bvid 看所有画质档 ——")
for r in c.execute("SELECT bvid, quality, COUNT(*) n FROM videos WHERE bvid!='' GROUP BY bvid, quality ORDER BY bvid"):
    print("    bvid=%s quality=%r ×%s" % (r["bvid"], r["quality"], r["n"]))
print()

with services.get_conn() as cc:
    bvid = cc.execute("SELECT bvid FROM videos WHERE id=58").fetchone()["bvid"]
info = bilibili.get_video_info(bvid)
cid = info["cid"]
print("=== 视频 %s cid=%s ===" % (bvid, cid))
print("=== 可选清晰度（界面里给用户选的那些）===")
for q in bilibili.get_available_qualities(bvid, cid):
    print("  qn=%-4s height=%-5s note=%s" % (q["qn"], q["height"], q["note"]))
print()
print("=== 不同 qn 请求时 get_playurl 实际返回 ===")
for qn in (120, 116, 80, 64, 32):
    try:
        s = bilibili.get_playurl(bvid, cid, qn=qn, hi_res=False)
        note = bilibili.QUALITY_NAMES.get(s["quality"], "?")
        print("  请求 qn=%-4s → 返回 quality=%-4s(%s) height=%-5s  期望高度=%s"
              % (qn, s["quality"], note, s["height"], bilibili._QN_HEIGHT.get(qn)))
    except Exception as e:
        print("  请求 qn=%-4s → 失败 %s: %s" % (qn, type(e).__name__, e))
