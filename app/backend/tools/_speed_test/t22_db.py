import json, sqlite3, sys, os

db = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db"
clipA = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\exports\【120FPS·杜比视界】洛天依《流光（Light Me Up）》【冰火歌会2025夏梦季】_10.0s-20.0s.mp4"
clipB = r"F:\DSH\LocalMediaPlayer-Workspace\app\data\exports\【120FPS·杜比视界】洛天依《流光（Light Me Up）》【冰火歌会2025夏梦季】_30.0s-40.0s.mp4"
mode = sys.argv[1] if len(sys.argv) > 1 else "setup"
state = r"F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_speed_test\t22_state.json"

con = sqlite3.connect(db); cur = con.cursor()

if mode == "setup":
    cur.execute("select id from videos order by id"); orig_v = [r[0] for r in cur.fetchall()]
    cur.execute("select id from playlists order by id"); orig_p = [r[0] for r in cur.fetchall()]
    cur.execute("select count(*) from enhance_tasks"); orig_e = cur.fetchone()[0]
    json.dump({"videos": orig_v, "playlists": orig_p, "enhance": orig_e}, open(state, "w"))

    ids = []
    # v1 = clipA；v2/v3 指向**同一个** clipB（复现用户场景：合集里两集指向同一个本地文件）
    for title, path in [("【T22自测】第1集 clipA", clipA), ("【T22自测】第2集 clipB", clipB), ("【T22自测】第3集 clipB-again", clipB)]:
        cur.execute("""insert into videos (bvid,title,up_name,local_path,quality,page,created_at,duration)
                       values ('',?,?,?,'测试',0,datetime('now','localtime'),10000)""", (title, "T22自测", path))
        ids.append(cur.lastrowid)
    cur.execute("insert into playlists (name, created_at) values (?, datetime('now','localtime'))", ("【T22自测】连播死循环复现（3集）",))
    pid = cur.lastrowid
    for i, vid in enumerate(ids):
        cur.execute("insert into playlist_items (playlist_id, media_type, media_id, position) values (?,?,?,?)", (pid, "video", vid, i))
    con.commit()
    print(json.dumps({"playlist_id": pid, "video_ids": ids, "clipA": os.path.basename(clipA), "clipB": os.path.basename(clipB)}, ensure_ascii=False))

elif mode == "cleanup":
    st = json.load(open(state))
    cur.execute("delete from playlist_items where playlist_id not in (%s)" % ",".join("?" * len(st["playlists"])), st["playlists"])
    cur.execute("delete from playlists where id not in (%s)" % ",".join("?" * len(st["playlists"])), st["playlists"])
    cur.execute("delete from videos where id not in (%s)" % ",".join("?" * len(st["videos"])), st["videos"])
    con.commit()
    cur.execute("select id from videos order by id"); now_v = [r[0] for r in cur.fetchall()]
    cur.execute("select id from playlists order by id"); now_p = [r[0] for r in cur.fetchall()]
    cur.execute("select count(*) from enhance_tasks"); now_e = cur.fetchone()[0]
    print("videos 还原 =", now_v == st["videos"], " playlists 还原 =", now_p == st["playlists"], " enhance_tasks =", now_e, "(原", st["enhance"], ")")
    print("删除后 videos 数 =", len(now_v), " playlists 数 =", len(now_p))

elif mode == "check":
    cur.execute("select id,name from playlists order by id")
    print("playlists:", cur.fetchall())
    cur.execute("select id,title,local_path from videos order by id desc limit 4")
    for r in cur.fetchall(): print("  video", r[0], r[1], "|", os.path.basename(r[2] or ""))
con.close()
