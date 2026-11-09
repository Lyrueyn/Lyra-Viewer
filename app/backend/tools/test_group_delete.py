"""验证"删除视频卡片"会连同一个视频的所有画质版本一起删掉（含文件）。

用假 bvid + 临时文件测试，不碰任何真实记录。
"""
import os
import sqlite3
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import services  # noqa: E402

BVID = "TESTBVID0001"
tmpdir = tempfile.mkdtemp(prefix="deltest_")
f1 = os.path.join(tmpdir, "test [720P].mp4")
f2 = os.path.join(tmpdir, "test [1080P].mp4")
for f in (f1, f2):
    with open(f, "wb") as fp:
        fp.write(b"x" * 1024)

with services.get_conn() as c:
    ids = []
    for q, p in (("720P", f1), ("1080P", f2)):
        cur = c.execute(
            "INSERT INTO videos (bvid, title, up_name, local_path, quality, page) "
            "VALUES (?, '测试视频', '测试', ?, ?, 1)", (BVID, p, q))
        ids.append(cur.lastrowid)
print("已插入测试记录:", ids)

rows = services.list_video_records(BVID, 1)
print("删除前 list_video_records →", [(r["quality"], r["has_file"]) for r in rows])
assert len(rows) == 2, "应有两条画质记录"

res = services.delete_video_by_bvid(BVID, delete_file=True, page=1)
print("delete_video_by_bvid →", res)

still = services.list_video_records(BVID, 1)
print("删除后 list_video_records →", [(r["quality"], r["has_file"]) for r in still])
print("文件是否还在: 720P=%s 1080P=%s" % (os.path.exists(f1), os.path.exists(f2)))

ok = (len(still) == 0) and (not os.path.exists(f1)) and (not os.path.exists(f2)) and res.get("deleted") == 2
print("结论:", "✔ 两条记录 + 两个文件都删干净了" if ok else "✘ 没删干净")

# 清理测试残留（正常路径已删文件，这里兜底删目录）
try:
    os.rmdir(tmpdir)
except OSError:
    pass
