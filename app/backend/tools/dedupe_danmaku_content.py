"""按"内容"给弹幕去重，并把约束从"仅无 id 行"升级为"所有行"。

背景（2026-10-14 晚，第二轮）：
  两个入库通道并存 ——
    ① 刷新流程（按日期翻历史/实时池）：带 dm_id
    ② 下载流程 `_save_danmaku`（seg.so 无 id）：dm_id 为空
  同一条弹幕经两条通道各存一份（内容完全相同、dm_id 一个有一个没有），
  按 id 的唯一索引管不到 → 库里 P1 变成 2135 行 / 内容去重仅 1066 行 → 画面上每条弹幕出现两遍。

本脚本：
  1) 备份数据库
  2) 按 (video_id,time,mode,size,color,text) 去重：**优先保留带 dm_id 的那行**（让按 id 的索引继续生效）
  3) 建全量内容唯一索引 idx_danmaku_content；删掉旧的"仅无 id 行"部分索引 idx_danmaku_noid
  4) VACUUM
用法：py -3.10 dedupe_danmaku_content.py [--apply]
"""
import datetime
import os
import shutil
import sqlite3
import sys

DB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
BAK_DIR = r"F:\DSH\LocalMediaPlayer-Workspace\backups\local-media-player-backup-20261014"
apply = "--apply" in sys.argv

c = sqlite3.connect(DB)
c.row_factory = sqlite3.Row

print("=== 清理前 ===")
for r in c.execute(
    "SELECT video_id, COUNT(*) total, "
    " COUNT(DISTINCT time||'|'||mode||'|'||size||'|'||color||'|'||text) uniq "
    "FROM danmaku GROUP BY video_id ORDER BY video_id"
):
    ratio = r["total"] / r["uniq"] if r["uniq"] else 0
    print("  video_id=%-4s 行数=%-6s 内容去重=%-6s 倍数=%.2f" % (r["video_id"], r["total"], r["uniq"], ratio))

dup = c.execute(
    "SELECT COUNT(*) FROM danmaku WHERE id NOT IN ("
    "  SELECT COALESCE(MIN(CASE WHEN dm_id IS NOT NULL THEN id END), MIN(id))"
    "  FROM danmaku GROUP BY video_id, time, mode, size, color, text)"
).fetchone()[0]
print("待删除的重复行: %s" % dup)
if not apply:
    print("（未加 --apply，未改动数据库）")
    sys.exit(0)

ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
bak = BAK_DIR + "\\local_media.db.bak-" + ts
shutil.copy2(DB, bak)
print("已备份 → %s" % bak)

try:
    c.execute(
        "DELETE FROM danmaku WHERE id NOT IN ("
        "  SELECT COALESCE(MIN(CASE WHEN dm_id IS NOT NULL THEN id END), MIN(id))"
        "  FROM danmaku GROUP BY video_id, time, mode, size, color, text)"
    )
    print("已删除重复行: %s" % c.total_changes)
    c.execute("DROP INDEX IF EXISTS idx_danmaku_noid")
    c.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_danmaku_content ON danmaku"
        "(video_id, time, mode, size, color, text)"
    )
    print("已建全量内容唯一索引 idx_danmaku_content（旧的 idx_danmaku_noid 已删除）")
    c.commit()
except Exception as e:
    c.rollback()
    print("失败已回滚: %s" % e)
    sys.exit(1)

c.execute("VACUUM")
c.commit()
print("=== 清理后 ===")
for r in c.execute("SELECT video_id, COUNT(*) c FROM danmaku GROUP BY video_id ORDER BY video_id"):
    print("  video_id=%-4s 行数=%s" % (r["video_id"], r["c"]))
print("索引:", [r["name"] for r in c.execute("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='danmaku'")])
print("数据库大小: %.2f MB" % (os.path.getsize(DB) / 1048576))
