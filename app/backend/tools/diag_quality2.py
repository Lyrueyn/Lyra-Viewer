"""诊断：为什么这个视频反复下都是 720P（界面列出的画质 ≠ 实际能下到的画质）。"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

bilibili.set_login_cookie(services.get_cookie())
bvid = "BV1Sf4y1q7H9"   # 杜比演示片（用户下了 10 次的那个）
info = bilibili.get_video_info(bvid)
pages = info.get("pages") or []
print("标题: %s" % info["title"][:60])
print("分P数: %s   B站弹幕总数: %s" % (len(pages), (info.get("stat") or {}).get("danmaku")))
print()

for pg in (pages or [{"page": 1, "part": "-", "cid": info["cid"]}]):
    pno = pg.get("page") or 1
    cid = pg["cid"]
    print("=== P%s  %s  cid=%s ===" % (pno, (pg.get("part") or "")[:40], cid))
    try:
        qs = bilibili.get_available_qualities(bvid, cid)
        print("  界面列出的可选画质: %s" % ", ".join("%s(%s)" % (q["note"], q["qn"]) for q in qs))
    except Exception as e:
        print("  可选画质查询失败: %s" % e)
        qs = []
    for qn in (120, 116, 112, 80, 64, 32):
        try:
            s = bilibili.get_playurl(bvid, cid, qn=qn, hi_res=False)
            note = bilibili.QUALITY_NAMES.get(s["quality"], "?")
            flag = "✔ 拿到目标档" if s["quality"] >= qn else "✘ 被降级"
            print("    请求 qn=%-4s → 实际 quality=%-4s(%-8s) height=%-5s %s"
                  % (qn, s["quality"], note, s["height"], flag))
        except Exception as e:
            print("    请求 qn=%-4s → 失败 %s" % (qn, e))
    print()
