"""确认 B站历史弹幕接口的语义：是"当天"还是"截至该日的快照"，并取最新快照补全。"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

bilibili.set_login_cookie(services.get_cookie())
with services.get_conn() as c:
    bvid = c.execute("SELECT bvid FROM videos WHERE id=60").fetchone()["bvid"]
cid = bilibili.get_video_info(bvid)["cid"]
dates = bilibili.fetch_history_danmaku_dates(cid)
print("日期数=%s  首=%s  末=%s" % (len(dates), dates[0], dates[-1]))

for label, d in (("首日", dates[0]), ("中间", dates[len(dates) // 2]), ("末日", dates[-1])):
    try:
        elems = bilibili.fetch_history_danmaku(cid, d)
        ids = {e.get("id") for e in elems}
        print("  %s %s → %s 条（唯一 id %s）" % (label, d, len(elems), len(ids)))
    except Exception as e:
        print("  %s %s → 失败 %s: %s" % (label, d, type(e).__name__, e))

live = bilibili.fetch_danmaku(cid)
print("  实时池 seg.so → %s 条" % len(live))
