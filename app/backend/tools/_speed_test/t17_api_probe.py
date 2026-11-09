import json, sqlite3, sys, urllib.request

db = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
base = "http://127.0.0.1:8765"

def post(url):
    data = json.dumps({"url": url}).encode()
    req = urllib.request.Request(base + "/api/download/submit", data=data,
                                headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=25) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except Exception as e:
        return "ERR", str(e)

con = sqlite3.connect(db); cur = con.cursor()
cur.execute("select max(id) from download_tasks"); max_id = cur.fetchone()[0] or 0
cur.execute("select url from download_tasks where id=81"); done_url = cur.fetchone()[0]
print("起始 max(id) =", max_id)
print("已下载过的 URL（任务 81）= ", done_url[:80])

fake = "https://www.bilibili.com/video/BV1T17tEsT"
print("\n--- 1) 提交一个不存在的 BV（第 1 次）---")
print(post(fake))
print("--- 2) 立刻再提交同一个 URL（第 2 次，看重复检测）---")
print(post(fake))
print("--- 3) 提交一个已经下载过的 URL（看 duplicate 分支）---")
print(post(done_url))

cur.execute("select id,status,substr(url,1,60),substr(coalesce(reason,''),1,30) from download_tasks where id > ? order by id", (max_id,))
rows = cur.fetchall()
print("\n本次新增行：", rows if rows else "无")

# 清理：只删本次测试 URL 的新增行（保护用户原有 17 条）
cur.execute("delete from download_tasks where id > ? and (url = ? or url = ?)", (max_id, fake, done_url))
n = cur.rowcount
con.commit()
cur.execute("select count(*) from download_tasks"); total = cur.fetchone()[0]
cur.execute("select count(*) from download_tasks where status='done'"); done = cur.fetchone()[0]
print("清理删除行数 =", n, " // 现有总行数 =", total, " // done =", done)
con.close()
