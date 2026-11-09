"""验证下载画质修复：对杜比演示片 P1 请求 1080P，看是否真的下到 1080P（而不是 720P）。"""
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import services  # noqa: E402

DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
URL = "https://www.bilibili.com/video/BV1Sf4y1q7H9"
BVID = "BV1Sf4y1q7H9"
QN = 80          # 1080P
PAGE = 1


def snapshot():
    c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
    c.row_factory = sqlite3.Row
    rows = [dict(r) for r in c.execute(
        "SELECT id, quality, page, local_path FROM videos WHERE bvid=? ORDER BY id", (BVID,))]
    c.close()
    for r in rows:
        p = r["local_path"]
        mb = os.path.getsize(p) / 1048576 if p and os.path.exists(p) else -1
        print("    id=%-4s quality=%-8s page=%s  %-52s %.1f MB"
              % (r["id"], r["quality"], r["page"], os.path.basename(p or ""), mb))
    return rows


print("=== 下载前库里的记录 ===", flush=True)
snapshot()

print("\n=== 提交下载：qn=%s(1080P) page=%s ===" % (QN, PAGE), flush=True)
tid = services.submit_download(URL, "bv*+ba/b", QN, False, PAGE)["task_id"]
print("  task_id=%s" % tid, flush=True)

t0 = time.time()
while True:
    c = sqlite3.connect("file:" + DB.replace("\\", "/") + "?mode=ro", uri=True)
    c.row_factory = sqlite3.Row
    r = c.execute("SELECT status, progress, error, local_path FROM download_tasks WHERE id=?", (tid,)).fetchone()
    c.close()
    st, pr = r["status"], r["progress"]
    print("  [%5.0fs] %-9s %5.1f%%  %s" % (time.time() - t0, st, pr, (r["error"] or "")[:60]), flush=True)
    if st in ("done", "failed", "cancelled"):
        break
    time.sleep(3)

print("\n=== 下载后库里的记录（应新增/更新为 1080P 一条，且文件与 720P 不同名）===", flush=True)
snapshot()
