import sqlite3, sys
con = sqlite3.connect(sys.argv[1]); cur = con.cursor()
cur.execute("update download_tasks set error=? where url=?", ("未达所选画质（仅 480P）", sys.argv[2]))
con.commit(); print("已给测试行补 error，影响行数 =", cur.rowcount); con.close()
