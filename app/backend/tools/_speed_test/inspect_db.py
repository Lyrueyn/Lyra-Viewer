import sqlite3, os, json
db = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
print("db 大小 =", os.path.getsize(db))
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
cur = con.cursor()
cur.execute("select name, sql from sqlite_master where type='table' and name like '%download%'")
for n, s in cur.fetchall():
    print("--- 表", n, "---"); print(s)
cur.execute("select count(*) from download_tasks"); print("download_tasks 行数 =", cur.fetchone()[0])
cur.execute("select status, count(*) from download_tasks group by status"); print("按状态:", cur.fetchall())
cur.execute("select max(id) from download_tasks"); print("max(id) =", cur.fetchone()[0])
cur.execute("select id,status,substr(coalesce(url,''),1,50),substr(coalesce(error,''),1,30),substr(coalesce(title,''),1,25) from download_tasks order by id desc limit 5")
for r in cur.fetchall(): print("  最近:", r)
con.close()
