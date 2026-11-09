"""一次性清理：删掉 danmaku 表里"无 dm_id 的重复行"。

背景（2026-10-14）：
  seg.so 弹幕接口不返回弹幕 id，走下载流程入库时 dm_id 全是空；
  而唯一索引 `idx_danmaku_dm` 带 `WHERE dm_id IS NOT NULL` 条件，约束不到这些行
  → 每次重新下载/刷新都整批追加一份。实测杜比演示片(video_id=60)被灌了 9 次：
  9621 行 / 去重后只有 1066 行，画面上每条弹幕出现 9 遍。

本脚本：
  1) 备份数据库到 D:\\DSH\\local-media-player-backup-20261014\\
  2) 无 dm_id 的行按 (video_id,time,mode,size,color,text) 去重，每组只留 id 最小的
  3) 建部分唯一索引 idx_danmaku_noid 防复发（有 dm_id 的行不动）
  4) VACUUM 回收空间
用法：py -3.10 dedupe_danmaku.py [--apply]   （不带 --apply 只统计，不写库）
"""
import datetime
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
    "SELECT video_id, COUNT(*) rows_total, "
    "  SUM(CASE WHEN dm_id IS NULL THEN 1 ELSE 0 END) rows_noid, "
    "  (SELECT COUNT(*) FROM (SELECT DISTINCT time,mode,size,color,text FROM danmaku x "
    "     WHERE x.video_id=d.video_id AND x.dm_id IS NULL)) rows_uniq "
    "FROM danmaku d GROUP BY video_id ORDER BY video_id"
):
    ratio = (r["rows_noid"] / r["rows_uniq"]) if r["rows_uniq"] else 0
    print("  video_id=%-4s 总=%-6s 无id=%-6s 去重=%-6s 倍数=%.2f"
          % (r["video_id"], r["rows_total"], r["rows_noid"], r["rows_uniq"], ratio))

dup = c.execute(
    "SELECT COUNT(*) FROM danmaku WHERE dm_id IS NULL AND id NOT IN ("
    "  SELECT MIN(id) FROM danmaku WHERE dm_id IS NULL"
    "  GROUP BY video_id, time, mode, size, color, text)"
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
        "DELETE FROM danmaku WHERE dm_id IS NULL AND id NOT IN ("
        "  SELECT MIN(id) FROM danmaku WHERE dm_id IS NULL"
        "  GROUP BY video_id, time, mode, size, color, text)"
    )
    print("已删除重复行: %s" % c.total_changes)
    c.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_danmaku_noid ON danmaku"
        "(video_id, time, mode, size, color, text) WHERE dm_id IS NULL"
    )
    print("已建部分唯一索引 idx_danmaku_noid（防复发）")
    c.commit()
except Exception as e:  # 建索引失败（仍有重复）→ 回滚，数据保持原样
    c.rollback()
    print("失败已回滚: %s" % e)
    sys.exit(1)

c.execute("VACUUM")
c.commit()

print("=== 清理后 ===")
for r in c.execute(
    "SELECT video_id, COUNT(*) c FROM danmaku GROUP BY video_id ORDER BY video_id"
):
    print("  video_id=%-4s 行数=%s" % (r["video_id"], r["c"]))
print("数据库大小: %.2f MB" % (__import__("os").path.getsize(DB) / 1048576))
