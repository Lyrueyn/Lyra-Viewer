import sqlite3, sys, json
con = sqlite3.connect(sys.argv[1]); cur = con.cursor()
cur.execute("delete from download_tasks where url = ?", (sys.argv[2],)); n = cur.rowcount
con.commit()
cur.execute("select id from download_tasks order by id"); now = [r[0] for r in cur.fetchall()]
orig = json.load(open(sys.argv[3]))
cur.execute("select count(*) from download_tasks where status='done'"); done = cur.fetchone()[0]
print("删除测试行数 =", n, " // 现在总行数 =", len(now), " // 原始 =", len(orig), " // 完全一致 =", now == orig, " // done 行数 =", done)
con.close()
