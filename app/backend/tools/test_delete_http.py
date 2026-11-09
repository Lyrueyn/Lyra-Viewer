"""端到端验证"删除视频卡片"走 HTTP 接口（?page=N 分组删除）——用假 bvid + 临时文件，不碰真实数据。

用法：cd backend && .venv\\Scripts\\python.exe tools\\test_delete_http.py
前提：后端已在 127.0.0.1:8765 运行（应用启动后会自动拉起；也可手动跑 run_backend.py）。
"""
import json
import os
import sqlite3
import sys
import tempfile
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import services  # noqa: E402

DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
BVID = "TESTDELHTTP1"
BASE = "http://127.0.0.1:8765"


def http_delete(url: str):
    req = urllib.request.Request(url, method="DELETE")
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


# 造两条假记录（同 bvid、同分P、两个画质）
tmpdir = tempfile.mkdtemp(prefix="delhttp_")
files = []
with services.get_conn() as c:
    ids = []
    for q in ("720P", "480P"):
        f = os.path.join(tmpdir, "t [%s].mp4" % q)
        with open(f, "wb") as fp:
            fp.write(b"x" * 2048)
        files.append(f)
        cur = c.execute(
            "INSERT INTO videos (bvid, title, up_name, local_path, quality, page) "
            "VALUES (?, '删除测试', '测试', ?, ?, 1)", (BVID, f, q))
        ids.append(cur.lastrowid)
print("已插入测试记录:", ids)

print("HTTP DELETE %s/api/videos/by-bvid/%s?page=1" % (BASE, BVID))
status, body = http_delete("%s/api/videos/by-bvid/%s?page=1" % (BASE, BVID))
print("  返回: HTTP %s  %s" % (status, body.strip()[:200]))

left = services.list_video_records(BVID, 1)
print("剩余记录:", [(r["quality"], r["has_file"]) for r in left])
print("文件是否还在:", [os.path.exists(f) for f in files])

ok = status == 200 and not left and not any(os.path.exists(f) for f in files)
print("结论:", "✔ 接口删除正常（记录 + 文件都删掉了）" if ok else "✘ 删除没生效")

if not ok:   # 失败时清理假数据，别留垃圾
    with services.get_conn() as c:
        c.execute("DELETE FROM videos WHERE bvid=?", (BVID,))
    for f in files:
        try:
            os.remove(f)
        except OSError:
            pass
    print("（已清理测试残留）")
try:
    os.rmdir(tmpdir)
except OSError:
    pass
