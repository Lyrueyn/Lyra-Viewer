import json, sqlite3, sys, time, urllib.request

db = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
base = "http://127.0.0.1:8765"
clip = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\exports\【120FPS·杜比视界】洛天依《流光（Light Me Up）》【冰火歌会2025夏梦季】_30.0s-40.0s.mp4"
state = r"F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test\t28_state.json"
mode = sys.argv[1] if len(sys.argv) > 1 else "setup"

def post(path, body):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:
        return "ERR", str(e)

def get(path):
    try:
        with urllib.request.urlopen(base + path, timeout=30) as r:
            return r.read().decode("utf-8", "replace")
    except Exception as e:
        return "ERR " + str(e)

def wait_done(tid, timeout=180):
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(2)
        try:
            body = json.loads(get("/api/enhance/chain/%d" % tid))
            st = body.get("task", {}).get("status", "")
            err = body.get("task", {}).get("error", "")
            if st in ("done", "failed", "cancelled"):
                return st, err, body
        except Exception:
            pass
    return "timeout", "", {}

if mode == "setup":
    print("1) 造 A：media_58 + 10 秒片段 + [响度统一]")
    st, resp = post("/api/enhance/submit_chain", {"media_id": 58, "media_type": "video", "source_path": clip,
                                                 "enhancers": ["loudness"], "device": "cuda"})
    print("   →", st, resp)
    A = json.loads(resp).get("task_id", 0)
    sa, ea, ba = wait_done(A)
    print("   A 状态 =", sa, "error =", ea)
    a_out = ba.get("task", {}).get("output_path", "")
    print("   A 产物 =", a_out)

    print("2) 造 B：对 A 再增强 [高精度重采样]（parent 应为 A）")
    st, resp = post("/api/enhance/submit_chain", {"media_id": 58, "media_type": "video", "source_path": a_out,
                                                 "enhancers": ["soxr_hires"], "device": "cuda"})
    print("   →", st, resp)
    B = json.loads(resp).get("task_id", 0)
    sb, eb, bb = wait_done(B)
    print("   B 状态 =", sb, "error =", eb)
    print("   B 的 chain:", json.dumps(bb, ensure_ascii=False)[:700])

    con = sqlite3.connect(db); cur = con.cursor()
    cur.execute("select id,parent_id,media_id,status from enhance_tasks where id in (?,?)", (A, B))
    rows = cur.fetchall(); con.close()
    print("   DB: id/parent/media/status =", rows)
    json.dump({"A": A, "B": B, "a_out": a_out, "pre_count": None}, open(state, "w"))

elif mode == "cleanup":
    st = json.load(open(state))
    ids = [st.get("B", 0), st.get("C", 0), st.get("A", 0)]
    con = sqlite3.connect(db); cur = con.cursor()
    for tid in ids:
        if not tid: continue
        # 直接删行 + 删产物文件（避免依赖接口的 409 保护）
        cur.execute("select output_path from enhance_tasks where id=?", (tid,))
        row = cur.fetchone()
        if row and row[0]:
            try:
                import os; os.remove(row[0])
            except Exception as e:
                print("   删文件失败", row[0], e)
        cur.execute("delete from enhance_tasks where id=?", (tid,))
        print("   已清理任务", tid)
    con.commit()
    cur.execute("select count(*) from enhance_tasks"); n = cur.fetchone()[0]
    cur.execute("select id from enhance_tasks order by id"); left = [r[0] for r in cur.fetchall()]
    con.close()
    print("enhance_tasks 现在 =", n, " ids =", left)

elif mode == "count":
    con = sqlite3.connect(db); cur = con.cursor()
    cur.execute("select count(*) from enhance_tasks"); print(cur.fetchone()[0])
    for t in ("videos", "download_tasks", "playlists", "playlist_items"):
        cur.execute("select count(*) from " + t); print(t, cur.fetchone()[0])
    con.close()
