"""SQLite 数据库：建表与基础访问。

表结构覆盖：歌曲 / 歌单 / 视频 / 弹幕 / 评论 / 下载任务 / 增强任务 / 历史记录。
"""
from __future__ import annotations

import sqlite3
from pathlib import Path

# 数据库统一放项目根 data/ 下（与 C++ 端 MediaLibrary 共用同一份媒体库）
DATA_DIR = Path(__file__).resolve().parent.parent.parent / "data"
DB_PATH = DATA_DIR / "local_media.db"

SCHEMA = """
CREATE TABLE IF NOT EXISTS songs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT UNIQUE NOT NULL,
    title TEXT DEFAULT '',
    artist TEXT DEFAULT '',
    album TEXT DEFAULT '',
    duration REAL DEFAULT 0,
    cover_path TEXT DEFAULT '',
    lyric_path TEXT DEFAULT '',
    format TEXT DEFAULT '',
    created_at TEXT DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS playlists (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    created_at TEXT DEFAULT (datetime('now', 'localtime'))
);

-- 歌单项：支持歌曲（media_type='song'）与视频（media_type='video'）
CREATE TABLE IF NOT EXISTS playlist_items (
    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    media_type TEXT DEFAULT 'song',
    media_id INTEGER NOT NULL,
    position INTEGER DEFAULT 0,
    PRIMARY KEY (playlist_id, media_type, media_id)
);

CREATE TABLE IF NOT EXISTS videos (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bvid TEXT,
    title TEXT NOT NULL,
    up_name TEXT DEFAULT '',
    cover_path TEXT DEFAULT '',
    desc TEXT DEFAULT '',
    local_path TEXT DEFAULT '',
    local_danmaku_path TEXT DEFAULT '',
    quality TEXT DEFAULT '',
    page INTEGER DEFAULT 0,   -- 分P序号（0=单视频/第一P，多P时区分各分P）
    created_at TEXT DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS danmaku (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
    dm_id INTEGER,
    time REAL NOT NULL,
    mode INTEGER DEFAULT 1,
    size INTEGER DEFAULT 25,
    color INTEGER DEFAULT 16777215,
    text TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS comments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
    rpid INTEGER,
    user_name TEXT DEFAULT '',
    content TEXT NOT NULL,
    like_count INTEGER DEFAULT 0,
    reply_to INTEGER,
    created_at TEXT DEFAULT ''
);

CREATE TABLE IF NOT EXISTS download_tasks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url TEXT NOT NULL,
    status TEXT DEFAULT 'pending',   -- pending / running / done / failed / cancelled
    progress REAL DEFAULT 0,
    local_path TEXT DEFAULT '',
    error TEXT DEFAULT '',
    cancel_requested INTEGER DEFAULT 0,   -- 用户点取消（下载线程检查后停止）
    created_at TEXT DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS enhance_tasks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_id INTEGER,
    media_type TEXT,                 -- audio / video
    enhancer TEXT NOT NULL,          -- realesrgan / denoise / loudness / audio_sr
    status TEXT DEFAULT 'pending',
    progress REAL DEFAULT 0,
    processed INTEGER DEFAULT 0,
    total INTEGER DEFAULT 0,
    cancel_requested INTEGER DEFAULT 0,
    output_path TEXT DEFAULT '',
    error TEXT DEFAULT '',
    created_at TEXT DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_type TEXT,
    media_id INTEGER,
    played_at TEXT DEFAULT (datetime('now', 'localtime'))
);

-- 弹幕抓取进度：记录每个视频已抓到的历史弹幕日期（断点续传用）
CREATE TABLE IF NOT EXISTS danmaku_progress (
    video_id INTEGER PRIMARY KEY,
    cid INTEGER DEFAULT 0,
    fetched_dates TEXT DEFAULT '[]',   -- JSON 数组：已抓取的日期 ["2025-08-24", ...]
    updated_at TEXT DEFAULT (datetime('now', 'localtime'))
);

-- 评论抓取进度：记录每个视频上次抓到的最大评论时间戳（增量抓取用）
CREATE TABLE IF NOT EXISTS comment_progress (
    video_id INTEGER PRIMARY KEY,
    last_ts INTEGER DEFAULT 0,   -- 上次抓到的最大评论 created_at（Unix 秒）
    updated_at TEXT DEFAULT (datetime('now', 'localtime'))
);
"""


def get_conn() -> sqlite3.Connection:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH, timeout=30)
    conn.row_factory = sqlite3.Row
    # WAL 模式：允许读写并发（弹幕刷新与评论后台线程同时写库时不互相阻塞）
    try:
        conn.execute("PRAGMA journal_mode=WAL")
    except Exception:
        pass
    return conn


def init_db() -> None:
    """建表（幂等，可重复调用）+ 轻量迁移（旧库补新列/索引）"""
    with get_conn() as conn:
        conn.executescript(SCHEMA)
        _migrate(conn)


def _migrate(conn) -> None:
    """旧库升级：danmaku 加 dm_id、comments 加 rpid，并建去重唯一索引。"""
    dm_cols = {r[1] for r in conn.execute("PRAGMA table_info(danmaku)")}
    if "dm_id" not in dm_cols:
        conn.execute("ALTER TABLE danmaku ADD COLUMN dm_id INTEGER")
    cm_cols = {r[1] for r in conn.execute("PRAGMA table_info(comments)")}
    if "rpid" not in cm_cols:
        conn.execute("ALTER TABLE comments ADD COLUMN rpid INTEGER")
    # 去重索引：同视频下 dm_id / rpid 唯一（NULL 不参与约束）
    conn.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_danmaku_dm "
        "ON danmaku(video_id, dm_id) WHERE dm_id IS NOT NULL")
    # ★ 2026-10-14：seg.so 抓弹幕不带 id（dm_id 为空）→ 上面那条索引约束不到，
    #   每次重新下载/刷新都会整批追加一份（实测杜比演示片被灌 9 次：9621 行 / 去重仅 1066 行）。
    #   升级为**全量内容唯一索引**：不管有没有 dm_id，同一条弹幕只存一份
    #   （两个入库通道：刷新流程带 id、下载流程 `_save_danmaku` 不带 id，以前会各存一份 → 画面上重复两遍）。
    conn.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_danmaku_content "
        "ON danmaku(video_id, time, mode, size, color, text)")
    conn.execute("DROP INDEX IF EXISTS idx_danmaku_noid")   # 已被上面这条覆盖，不再需要
    conn.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_comments_rpid "
        "ON comments(video_id, rpid) WHERE rpid IS NOT NULL")
    # ★ 2026-10-14：合集条目去重 + 唯一索引（同合集同媒体只允许一条）
    #   以前只 INSERT OR IGNORE，但没有唯一约束 → 重复加入会产生重复条目/孤儿条目（media_id=0）。
    try:
        conn.execute(
            "DELETE FROM playlist_items WHERE rowid NOT IN ("
            "  SELECT MIN(rowid) FROM playlist_items GROUP BY playlist_id, media_type, media_id)")
        conn.execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_playlist_items_uniq "
            "ON playlist_items(playlist_id, media_type, media_id)")
    except Exception:
        pass
    # 首页手动排序（拖拽调整视频卡片顺序；key = bvid-page 或 local<id>）
    conn.execute(
        "CREATE TABLE IF NOT EXISTS video_order ("
        "  key TEXT PRIMARY KEY,"
        "  pos INTEGER NOT NULL DEFAULT 0"
        ")"
    )
    # playlist_items 排序：position 列已有，这里只需保证存在    # videos 升级：subtitle_path（下载时抓的字幕文件路径）
    vd_cols2 = {r[1] for r in conn.execute("PRAGMA table_info(videos)")}
    if "subtitle_path" not in vd_cols2:
        conn.execute("ALTER TABLE videos ADD COLUMN subtitle_path TEXT DEFAULT ''")    # videos 升级：pub_time（视频发布日期，Unix 秒；以前界面显示的是下载入库时间）
    vd_cols = {r[1] for r in conn.execute("PRAGMA table_info(videos)")}
    if "pub_time" not in vd_cols:
        conn.execute("ALTER TABLE videos ADD COLUMN pub_time INTEGER DEFAULT 0")    # comments 升级：reply_count（回复数，判断是否有楼中楼）
    cm2 = {r[1] for r in conn.execute("PRAGMA table_info(comments)")}
    if "reply_count" not in cm2:
        conn.execute("ALTER TABLE comments ADD COLUMN reply_count INTEGER DEFAULT 0")
    # root：楼中楼顶层 rpid（嵌套回复也归到正确的楼，避免"显示 N 条只有 N-1 条"）
    if "root" not in cm2:
        conn.execute("ALTER TABLE comments ADD COLUMN root INTEGER")
    # enhance_tasks 升级：processed/total（帧进度）、cancel_requested（取消）
    et_cols = {r[1] for r in conn.execute("PRAGMA table_info(enhance_tasks)")}
    if "processed" not in et_cols:
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN processed INTEGER DEFAULT 0")
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN total INTEGER DEFAULT 0")
    if "cancel_requested" not in et_cols:
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN cancel_requested INTEGER DEFAULT 0")
    # source_path：链式增强的输入文件（如上一增强版的输出），空=原始媒体
    if "source_path" not in et_cols:
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN source_path TEXT DEFAULT ''")
    # parent_id：父增强任务 id（0=来自原始媒体），用于版本血缘/编号（降噪#1 → 降噪#1响度#1）
    if "parent_id" not in et_cols:
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN parent_id INTEGER DEFAULT 0")
    # ★ 2026-10-17（T27）：enhancers_json —— 增强链的**节点序列**（JSON 数组，如 ["soxr_hires","loudness"]）。
    #   决策：**一条链 = 一条 enhance_tasks 记录 + 节点序列**（不是一节点一条记录）。
    #   老的 enhancer 列保留（写第一个节点），兼容旧代码/旧记录（读取时用 _task_enhancers() 兼容两者）。
    if "enhancers_json" not in et_cols:
        conn.execute("ALTER TABLE enhance_tasks ADD COLUMN enhancers_json TEXT DEFAULT ''")
    # download_tasks 升级：cancel_requested（用户取消下载）
    dt_cols = {r[1] for r in conn.execute("PRAGMA table_info(download_tasks)")}
    if "cancel_requested" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN cancel_requested INTEGER DEFAULT 0")
    if "title" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN title TEXT DEFAULT ''")
    # ★ 2026-10-17（T15/A2-9）：updated_at —— 用来判断"任务是不是已经死了"
    #   以前只有 created_at，进程被杀后 pending/running 永远留着，界面永远转圈。
    if "updated_at" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN updated_at TEXT")
        try:
            conn.execute(
                "UPDATE download_tasks SET updated_at=COALESCE(created_at, datetime('now','localtime')) "
                "WHERE updated_at IS NULL")
        except Exception:
            pass
    if "reason" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN reason TEXT DEFAULT ''")
    # ★ 2026-10-17（T17 实测发现）：把**下载参数**也存进任务行。
    #   问题：界面的"失败重试"是拿任务行的 URL 重新 submit，而 qn/hi_res/page 没存过 →
    #   重试一律用默认 qn=120 + hi-res → **用户当初特意选 480P 的视频，重试会变成 4K 重下**。
    #   存下来之后，列表接口返给前端，重试时原样带回即可。
    if "qn" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN qn INTEGER DEFAULT 120")
    if "hi_res" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN hi_res INTEGER DEFAULT 1")
    if "page" not in dt_cols:
        conn.execute("ALTER TABLE download_tasks ADD COLUMN page INTEGER DEFAULT 0")
    # ★ 2026-10-17（T15）：**自动心跳触发器** —— 任何对 download_tasks 的 UPDATE
    #   都会刷新 updated_at，不需要去改 12 处调用点（也不会漏掉将来新增的调用点）。
    #   僵尸任务自愈靠 updated_at 判断"这个任务还活着吗"。
    #   ★ 递归说明：SQLite 默认 recursive_triggers=OFF，内层 UPDATE 不会再次触发；
    #     即便被打开，`WHEN NEW.updated_at IS OLD.updated_at` 也保证只在"没主动改时间"时才补。
    try:
        conn.execute(
            "CREATE TRIGGER IF NOT EXISTS trg_download_tasks_touch "
            "AFTER UPDATE ON download_tasks FOR EACH ROW "
            "WHEN NEW.updated_at IS OLD.updated_at "
            "BEGIN UPDATE download_tasks SET updated_at=datetime('now','localtime') "
            "WHERE id=NEW.id; END")
    except Exception as e:
        print("[db] ⚠ 创建 updated_at 心跳触发器失败（不影响运行）: %s" % e)
    # ★ 2026-10-17（T15/A2-9）：videos 的**业务唯一键** (bvid, quality, page)。
    #   症状：表里从来没有唯一约束，代码里的 `INSERT OR IGNORE INTO videos(...)` 是**空保险**
    #   → 同一 bvid+quality+page 再下一次就会插两行、同一文件落两遍（只是还没发生）。
    #   ★ 先做全表校验：**发现冲突就报告并跳过建索引，绝不静默删用户数据**。
    #   ★ 空 bvid（非 B 站源）不纳入唯一键，用 partial index 排除。
    try:
        dup_rows = conn.execute(
            "SELECT bvid, quality, page, COUNT(*) c FROM videos "
            "WHERE bvid IS NOT NULL AND bvid != '' "
            "GROUP BY bvid, quality, page HAVING c > 1 LIMIT 5").fetchall()
    except Exception:
        dup_rows = []
    if dup_rows:
        print("[db] ⚠ videos 存在同 (bvid,quality,page) 的重复行 %d 组，**跳过**唯一索引创建：%s"
              % (len(dup_rows), [tuple(r) for r in dup_rows]))
    else:
        conn.execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_videos_uniq "
            "ON videos(bvid, quality, page) WHERE bvid IS NOT NULL AND bvid != ''")
    # videos 升级：duration（毫秒，卡片右下角显示时长用）
    vid_cols = {r[1] for r in conn.execute("PRAGMA table_info(videos)")}
    if "duration" not in vid_cols:
        conn.execute("ALTER TABLE videos ADD COLUMN duration INTEGER DEFAULT 0")
    _migrate_playlist_items(conn)
    _migrate_videos(conn)


def _migrate_playlist_items(conn) -> None:
    """playlist_items 升级：旧表只有 song_id，重建为通用 (media_type, media_id) 结构。

    旧数据（song_id）搬进新结构（media_type='song', media_id=song_id）。
    """
    cols = {r[1] for r in conn.execute("PRAGMA table_info(playlist_items)")}
    if "media_type" in cols:
        return  # 已是新结构
    conn.executescript("""
    CREATE TABLE IF NOT EXISTS playlist_items_new (
        playlist_id INTEGER NOT NULL,
        media_type TEXT DEFAULT 'song',
        media_id INTEGER NOT NULL,
        position INTEGER DEFAULT 0,
        PRIMARY KEY (playlist_id, media_type, media_id)
    );
    INSERT INTO playlist_items_new (playlist_id, media_type, media_id, position)
        SELECT playlist_id, 'song', song_id, position FROM playlist_items;
    DROP TABLE playlist_items;
    ALTER TABLE playlist_items_new RENAME TO playlist_items;
    """)


def _migrate_videos(conn) -> None:
    """videos 表升级：加 quality + page 列；去掉 bvid 唯一约束（支持同视频多画质/多分P共存）。"""
    cols = {r[1] for r in conn.execute("PRAGMA table_info(videos)")}
    if "quality" in cols and "page" in cols:
        return  # 已是最新
    # 重建表（数据复制到新表，不丢数据；旧库 page 默认 0）
    conn.executescript("""
    CREATE TABLE IF NOT EXISTS videos_new (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        bvid TEXT,
        title TEXT NOT NULL,
        up_name TEXT DEFAULT '',
        cover_path TEXT DEFAULT '',
        desc TEXT DEFAULT '',
        local_path TEXT DEFAULT '',
        local_danmaku_path TEXT DEFAULT '',
        quality TEXT DEFAULT '',
        page INTEGER DEFAULT 0,
        created_at TEXT DEFAULT (datetime('now','localtime'))
    );
    INSERT INTO videos_new (id, bvid, title, up_name, cover_path, desc, local_path, local_danmaku_path, quality, page, created_at)
        SELECT id, bvid, title, up_name, cover_path, desc, local_path, local_danmaku_path, quality, 0, created_at FROM videos;
    DROP TABLE videos;
    ALTER TABLE videos_new RENAME TO videos;
    """)
