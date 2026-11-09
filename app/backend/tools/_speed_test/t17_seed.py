import sqlite3, sys, json
db = sys.argv[1]; url = sys.argv[2]
con = sqlite3.connect(db); cur = con.cursor()
cur.execute("select id from download_tasks order by id"); orig = [r[0] for r in cur.fetchall()]
open(sys.argv[3], "w").write(json.dumps(orig))
cur.execute("""insert into download_tasks (url,status,progress,local_path,error,title,reason,created_at,updated_at)
               values (?,?,?,?,?,?,?,datetime('now','localtime'),datetime('now','localtime'))""",
            (url, 'failed', 0, '', '', '【T17自测】失败任务：显示原因 + 一键重试', '进程中断（超过 10 分钟无进度更新）'))
con.commit()
print("原始任务数 =", len(orig), "  // 插入测试行 id =", cur.lastrowid)
con.close()
