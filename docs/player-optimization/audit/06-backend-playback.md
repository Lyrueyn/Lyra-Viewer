# 后端播放相关服务审计报告

> 审计对象：`app\backend\app`（只读，未改任何文件、未启动服务）。行号基准：`stdlib_server.py` 453 行、`services.py` 4081 行、`db.py` 336 行、`bilibili.py` 911 行、`Backend.cs` 625 行（任务书的 426/3631 是旧快照，已按实际文件重新定位）。

## 0. 一句话结论

**播放器不依赖后端**——引擎 `DshEngine_OpenMedia(path)` 直读本地文件，后端零流式接口，所以"后端崩了不能播"不是风险；真正的风险是**元数据/弹幕/字幕链被重活和长任务堵死**：`/api/videos` 在请求线程里逐条 spawn ffmpeg，`/api/videos/{id}/refresh` 用单事务跑几分钟，而 HTTP 层无超时、无并发上限——一个慢请求就能让整个播放信息面板假死。

## 1. 现状架构摘要

**活代码**：`run_backend.py`（16 行，只 import `app.stdlib_server.main`）→ `stdlib_server.py`（唯一 HTTP 入口，`ThreadingHTTPServer`）→ `services.py` + `bilibili.py`/`downloader.py`/`ffmpeg_tools.py`/`db.py`/`playlists_db.py`/`ai_subtitle.py`/`ai_pipeline.py`/`enhancers/*`。

**死代码**：`app/api.py`（363 行 FastAPI，全仓 grep 无任何 import；`run_backend.py` 只走 stdlib）。它自带一份**已漂移**的 `_run_download`/`_video_to_db`/`sweep_zombie_tasks` 和 stdlib 版不存在的 `/api/download/retry`，而 `requirements.txt` 仍在装 `fastapi+uvicorn`。

**播放链路**：全目录 grep `Range|206|Accept-Ranges` 只命中 `bilibili.py:828` 的下载请求头 → 后端**没有任何视频流接口**，在线视频只有"下载后播放"，无直链流播；弹幕/字幕是下载时入库、播放时查库（例外见 P0-2/P1-2）。

**数据层**：`app/data/local_media.db`（WAL，与 C++ MediaLibrary 共用）；`db.py:129-138` `get_conn()` 每次新建连接、只设 `PRAGMA journal_mode=WAL`、**不 close**。

## 2. 问题清单

### P0-1 HTTP 层无超时、无体量/并发上限，异常原文回吐前端

**证据**：`stdlib_server.py:22-24` 只有 `protocol_version`，**没有** `timeout` 类属性 → 半开连接上 `rfile.readline()` 永久阻塞；`:444` `ThreadingHTTPServer` 无 worker 上限；`:43-52` `_read_json` 按 `Content-Length` 一次读满、无上限；`:39-40` + `:140-142` 任何异常 `_send_error(500, str(e))` 把路径/SQL 原文返回。

**影响/根因**：App 被强杀留下的半开连接各占一线程且永不释放（线程无上限→内存与句柄线性涨，症状是"后端活着但所有接口超时"）。前端超时 30s（`Backend.cs:21`）与服务端 120s（`media_info`）/600s（`export_clip`）/无限（`refresh_video`）完全错配，长任务必然前端先超时、后端继续跑。

**方案**：`Handler.timeout = 30`；自定义 `BoundedThreadingHTTPServer`（`BoundedSemaphore(16)`，无令牌即 503）；`_read_json` 加 `MAX_BODY=1MB`（超限 413）；统一错误体 `{"error":{"code","message"}}`，堆栈只进日志。

**验收**：空连接不发数据 30s 内释放线程；`ab -c 100` 打 `/health` 无 5xx 雪崩、P99<200ms；500 响应不含 `Traceback`/绝对路径。**工作量 0.5 人日。**

### P0-2 `/api/videos` 把"首页列表"做成重任务，每次请求还新起后台线程

**证据**：`services.py:1921` 在 for 循环里逐条 `_video_duration_ms(main)`，`:1966-1987` 未命中即 `subprocess.run([ff,"-i",path], timeout=20)` 并单独开连接写库；`:1934-1937` 只要有缺画质/缺封面就 `threading.Thread(...).start()`（`_backfill_local_quality:1862-1886` 一次最多 200 次 ffprobe；`_backfill_covers:1580` 有 300s 节流）。

**影响/根因**：前端 `VideosAsync()` 是播放前必调（`VideoWindow.xaml.cs:5550` 前后遍历匹配当前文件）。28 条库冷缓存最坏 28×20s 串行；缺封面时每次请求新增一个抓取线程。根因是把"补数据"混进只读接口，且没有单例 worker 闸门。

**方案**：`/api/videos` **绝不 spawn 子进程**（duration 缺则返回 0），补数收进单例 `_meta_worker`（`queue.Queue` + 一个守护线程）串行处理并把三处 backfill 全并入；另加 `?fields=summary` 轻量模式供播放页用。

**验收**：28 条库冷启动 P95<50ms（现最坏 >10s）；连续 10 次请求 ffmpeg 子进程恒 ≤1；`threading.active_count()` 不增长。**工作量 1 人日。**

### P0-3 `/api/videos/{id}/refresh` 在请求线程同步抓全量历史弹幕，单事务长写锁

**证据**：`services.py:2299-2319` 在**一个** `with get_conn()` 事务里 `for date, elems in bilibili.fetch_all_danmaku_incremental(...)` 逐天抓网 + 逐条 INSERT + 每天插进度；`bilibili.py:298-323` 遍历历史每一天、每天一次 HTTP（15s 超时、无重试）。前端为此专开 15 分钟长超时（`Backend.cs:23`、`:390-396`）。

**影响/根因**：一次刷新=请求线程被占几十分钟 + SQLite 写锁长期持有（`timeout=30` 后其它写请求直接抛 `database is locked`），期间播放页的 `/api/videos`、`/danmaku`、`/stats` 同进程抢连接。长任务没有任务化，仍是同步 RPC 语义。

**方案**：照 `download_tasks` 建 job 表：`POST /refresh` 只插一条 job + 起线程，**立即 202+job_id**；worker **按天独立事务**提交并写进度；新增 `GET /api/videos/{id}/refresh/progress` 供轮询。

**验收**：`POST` 返回 <50ms；刷新中 `/api/videos` P95<100ms、零 `database is locked`；前端可删掉 15 分钟长超时。**工作量 1.5 人日。**

### P0-4 本地服务没有安全边界：CORS `*` + 无鉴权 + path 全放行

**证据**：`stdlib_server.py:35` 每个响应带 `Access-Control-Allow-Origin: *`（无 Origin 校验、无 token）；`:72-74` `GET /api/media/info?path=<任意路径>` 直接进 `media_info`（`services.py:3401-3406` 只 `p.exists()` 就 ffprobe）；`product_info`（`:3918-3925`）、`import_local_video`（`:245-249`）、`ai_subtitle.start_generate`（`ai_subtitle.py:564-567`）同样吃任意 path。

**影响/根因**：用户浏览任意恶意页面时，可用**无需预检的简单 GET** 拉走整个媒体库（标题+本地绝对路径，`GET /api/videos`）并探测任意文件存在/大小（`?path=C:\...`）；DELETE/POST 虽被预检挡住，但"库+路径全量泄漏"已成事实。根因是 WinUI 用 `HttpClient` 直连、本不需要 CORS，为将来浏览器调试顺手放开了。

**方案**：① 去掉 ACAO（或只对 `Origin` ∈ {`null`,`http://localhost:PORT`} 回显）；② 启动生成 32B 随机 token 写 `data/backend_token`，前端读后带 `X-DSH-Token`，缺失 401；③ 新增 `_guard_media_path(p)`：`Path(p).resolve()` 必须落在 `DATA_DIR`/`DOWNLOAD_DIR`/`ENHANCED_DIR`/`local_videos` 之下，否则 403，`media_info`/`product_info`/`export_clip`/`ai_subtitle.generate`/`import` 全走它。

**验收**：`curl -H "Origin: https://evil.com" .../api/videos` 响应无 ACAO；`?path=C:\Windows\win.ini` 返回 403；正常播放路径不受影响。**工作量 0.5 人日**（仅 CORS+白名单 0.2）。

### P1-1 `api.py` 是死代码，且是与活代码已分叉的复制品

**证据**：`api.py:26-33` `FastAPI(...)`；`:115`/`:168`/`:207` 各有一份 `_run_download`/`_video_to_db`/`sweep_zombie_tasks`；`:228` `/api/download/retry` 在 stdlib 版不存在；grep `app.api` 只命中两处注释。
**影响/方案**：谁照它改就制造分叉 bug；`requirements.txt` 白装约 30MB 且破坏"标准库零依赖"承诺 → 删 `api.py`，requirements 只留 `yt-dlp`。**验收**：grep `fastapi|uvicorn` 0 命中，`import app.stdlib_server` 正常。**工作量 0.2 人日。**

### P1-2 字幕抓取：8 轮重试且每轮重复请求 view 接口，卡在下载收尾

**证据**：`services.py:1269-1307` `for _attempt in range(8)`，每轮 `bilibili.get_player_subtitles(bvid,cid)`，而 `bilibili.py:114` 内部**又**调一次 `get_video_info`（外层 `:1261` 已拿过同一份）；`:1304` 空列表 `sleep(1.0)`。最坏 ≈ 8×(1 view + 1 player/v2 + N 字幕体)。
**影响/方案**：`_run_download:715-718` 在**下载完成后**同步调它 → 进度条 100% 却迟迟不 `done`。改：`get_player_subtitles(..., info=None)` 复用已取的 info；退避 3 轮 + 总预算 15s（`time.monotonic()`）；把抓字幕移出下载线程成独立 job。**验收**：一次抓取 view 请求 8→1；下载不再被字幕拖慢。**工作量 0.8 人日。**

### P1-3 `video_stats` 每次播放都联网

**证据**：`services.py:1126-1153` 只要 `bvid` 非空就 `bilibili.get_video_info(...)`，**零缓存**（对比 `video_intro:4011-4036` 有回写缓存）。
**影响/方案**：`VideoWindow.xaml.cs:5591-5621` 播放时**串行**发 4 个请求（comments→intro→stats→danmaku），stats 是纯外网 RTT，断网时 15s。改：`videos` 加 `view_count`/`dm_count`/`stat_at` 三列，`video_stats` 只读库、超 6h 才后台刷；前端 4 请求改 `Task.WhenAll`。**验收**：离线仍能显示统计；面板总耗时≈max 而非 sum。**工作量 0.7 人日。**

### P1-4 弹幕/评论全量 JSON 无压缩，切画质就重拉

**证据**：`services.py:2097-2103` 全表 `SELECT ... ORDER BY time`；`stdlib_server.py:30-37` 无 gzip；前端 `LoadDanmakuAsync`（`VideoWindow.xaml.cs:5031`、`:5152`）每次重拉；`video_comments:2123` 在 `with get_conn()` 块**外**继续用 `c`（依赖"连接未 close"这个实现细节），且每条顶层评论再查一次（N+1）。
**影响/方案**：1 万条弹幕 ≈1.4MB，切一次画质重下 1.4MB。改：`_send` 支持 gzip；弹幕加 `?since=` 与 `ETag`（`MAX(id)`+`COUNT(*)`）命中 304；评论预览用一次 `IN(...)` 分组查询；连接使用收进 with。**验收**：体积 -85%、弹幕就绪<100ms；评论查询 21→2。**工作量 0.6 人日。**

### P1-5 SQLite 写放大与连接滥用（与增强/下载抢锁）

**证据**：`db.py:129-138` 每次 `get_conn()` 都 `mkdir`+`connect`+`PRAGMA WAL`、从不 close（`with conn` 只 commit）；`services.py:602-615` 下载进度每个 hook 一次连接两条语句；`:2869-2879` 增强进度同理；`:1640-1651` `list_download_tasks` **每次查询前**先跑 `sweep_zombie_tasks()`（一条 UPDATE），而前端每 1.5s 轮询（`MainWindow.xaml.cs:175`）→ 常驻 ~40 次写/分钟。
**影响/方案**：播放读与这些写交错，WAL 抖动时读也 30s 超时。改进度写节流 200ms（内存值+定时 flush）；僵尸扫描改启动时+每 60s；`get_conn` 改 `contextmanager` 保证 `finally: close()`，WAL 只首次设（模块级标志）；高频读用 `threading.local()` 复用连接。**验收**：空闲时 `-wal` 写入≈0；连接数/句柄不随调用增长。**工作量 1 人日。**

### P1-6 僵尸后端与端口自愈会误杀、双实例共库

**证据**：`stdlib_server.py:413-416` 用 `wmic` 取命令行——**Win11 24H2 已移除 wmic**，此时 `cli=""` 走 `:420-422` 兜底 `_is_python(pid)`，等于"凡是监听该端口的 python.exe 一律 `taskkill /F`"；`:443` 只清理**自己**的 `args.port`，而前端会挑 8766+（`Backend.cs:74-81`）→ 8765 上的旧后端永不被清；`db.py:11-12` 用 `__file__` 定位数据库，与端口无关。
**影响/方案**：双后端并存写同一份库（历史"串库"的复现路径），兜底还可能杀掉用户自己的 python 服务。改：删 `_is_python` 兜底（拿不到命令行只记日志）；端口改 `--port 0` 由后端绑定后写 `data/backend_port.json`（含 pid/启动时间）；写库前用 `msvcrt.locking` 拿 `data/backend.lock`，拿不到就以只读模式启动并提示。**验收**：预置无关 python 占 8765，启动后它仍存活且应用正常连 8766；二次启动日志出现"已有实例"。**工作量 1 人日。**

### P1-7 后端日志是黑洞，前端日志无上限

**证据**：`stdlib_server.py:440` `basicConfig` 默认写 stderr，而 `Backend.cs:83-88` `Process.Start` 未重定向 stdout/stderr、WinUI 无控制台 → `logger.exception` 全丢（`backend_err.log` 实测 63 字节、`app/data/backend_out.log` 0 字节）。前端 `VideoWindow.xaml.cs:183-191` `DmLog` = `File.AppendAllText`，无上限无轮转。
**影响/方案**：后端崩溃零证据；`dm_debug.log` 无限涨（用户反馈 6MB+）。改：`RotatingFileHandler(data/logs/backend.log, 5MB, 3)`；启动写一行 `ffmpeg_tools.describe()`（现成函数 `:171-176`）；前端 `DmLog` 走队列 + 8MB 截断。**验收**：故意抛异常后日志有完整 traceback；跑 1 小时 `dm_debug.log` ≤8MB。**工作量 0.5 人日。**

### P1-8 "子进程失败当成功"只在 media_info 修了

**证据**：`services.py:3428-3432` 已正确校验"无 format 也无 streams → 报错带 stderr 尾巴"（好）。但 `_video_duration_ms:1977-1986` 只看正则、失败 `return 0`，无法区分"没有时长"与"ffmpeg 起不来"；`ai_subtitle._probe_duration:435-445` 同样返回 0.0；`ffmpeg_tools.probe_color:230-233` 异常直接判"非 HDR"。
**影响/方案**：安装版缺 DLL/解码器时表现为"时长 0/当作 SDR"。改：抽 `_run_probe(argv)->(rc,out,err)`，rc≠0 或输出空必须 `logger.warning` 带 stderr 末 200 字，返回值改 `(value, error)`。**验收**：把 ffmpeg 改名后各接口返回可读原因而非 0/未知。**工作量 0.6 人日。**

### P2（择要）

1. `export_clip`（`services.py:2656` timeout=600s）在请求线程跑 ffmpeg，前端 30s 超时后重试会再起一个进程 → 任务化 + 同视频串行。
2. `product_info:3929-3936` 兜底全表扫 300 行做 normpath 比较 → 给 `enhance_tasks.output_path` 建索引或存派生列。
3. `import_local_video:2021-2025` `shutil.copy2` 大文件同步阻塞（靠 15 分钟长超时掩盖）→ 任务化 + 进度。
4. `ai_subtitle.py:450` 临时文件全局固定 `DATA_DIR/temp_whisper.wav`（双实例互踩）；`:499-501` 整段读入内存（2h ≈700MB 峰值）；`:166-184` 转写过程中 `pip install zhconv` → `mkstemp` + 流式分块 + 依赖前置。
5. `_read_json:43-50` 不解 chunked（只警告）；前端已用 `StringContent` 绕过（`Backend.cs:369-374`）→ 实现 chunked 或对 chunked 返回 411。
6. 错误结构四不像：`{"ok":false,"error"}`（多数）、`{"detail"}`（`_send_error`）、`{"status":"error","error"}`（`services.py:452`）、`{"conflict":true}`→409（`stdlib_server.py:282-283`）→ 统一 `{code,message,detail}`。
7. 死分支：`stdlib_server.py:82-86` GET `/api/download/playlist-items` 用了 do_GET 里不存在的 `body`，`:236-238` POST `/api/videos/intro` 用了 do_POST 里不存在的 `q`，两者都会 NameError→500（当前前端走的是 GET intro / POST playlist-items，故为**潜伏 bug**，10 分钟可修）。
8. `db.py:164-166` 把 `text` 放进唯一索引，1 万条弹幕时索引≈数据本身 → 改 `HASH(text)` 生成列索引。

## 3. 优化方案（按收益排序）

1. **列表接口去重活**（P0-2）：`/api/videos` 零子进程 + 单例补数 worker → 播放前元数据从"最坏 10s+"到 <50ms，线程雪崩消失。
2. **长任务任务化**（P0-3、P2-1/3）：refresh/字幕/导出/导入统一 job 表 + 进度接口 + 按天提交 → 请求线程不被占、前端删掉 15 分钟长超时、SQLite 无长写锁。
3. **HTTP 加固**（P0-1）：handler timeout + 线程上限 + body 上限 + 统一错误体 → 慢连接/异常不再拖死后端。
4. **安全边界**（P0-4）：去 ACAO + token + 路径白名单 → 堵住任意网页枚举媒体库。
5. **元数据不联网**（P1-3）：stats 落库 + 前端 4 请求并行 → 离线可用、延迟 sum→max。
6. **字幕/弹幕提效**（P1-2、P1-4）：view 复用 + 退避，gzip + ETag → 请求数 8→1、体积 -85%。
7. **DB 与可观测性**（P1-5、P1-7、P1-8）：连接托管 + 进度节流 + 日志轮转 + rc 判据。
8. **清死代码**（P1-1）：删 `api.py`、requirements 只留 yt-dlp。

**本轮不建议做**：给后端加 HTTP 视频流（Range/206）与直链在线播放——播放栈是本地引擎直读，加流播等于重建；在线视频缺的是"边下边播"的产品决策，不是后端能力缺口。

## 4. 推荐实施顺序（Top 10）

| 序 | 动作 | 模块 | 收益 | 工作量 | 风险 |
|---|---|---|---|---|---|
| 1 | `Handler.timeout=30` + 线程上限 16 + `MAX_BODY` + 500 不回吐原文 | stdlib_server | 高 | 0.5d | 低 |
| 2 | `/api/videos` 去 ffmpeg 化 + 单 worker 补数 | services | 高 | 1d | 中 |
| 3 | 去 ACAO + `X-DSH-Token` + `_guard_media_path` | stdlib_server/services | 高 | 0.5d | 中（前端同步带头） |
| 4 | refresh/字幕/导出改 job + 进度接口 + 按天提交 | services | 高 | 2d | 中 |
| 5 | 删 `api.py`、requirements 只留 yt-dlp | repo | 中 | 0.2d | 低 |
| 6 | DB：contextmanager close + 进度节流 + 僵尸扫描降频 | db/services | 中 | 1d | 中 |
| 7 | `video_stats` 落库缓存 + 前端 4 请求 `WhenAll` | services/VideoWindow | 中 | 0.7d | 低 |
| 8 | 字幕传 info 复用 + 3 轮退避 + 移出下载线程 | bilibili/services | 中 | 0.8d | 低 |
| 9 | 弹幕 gzip + ETag | stdlib_server/services | 中 | 0.6d | 低 |
| 10 | RotatingFileHandler + 启动自检 + 前端日志截断 | 全链 | 中 | 0.5d | 低 |

合计约 **10 人日**；只做前 3 项（1.9d）即可消除"播放信息面板假死/后端被拖死"两类 P0 症状。

## 5. 审计覆盖与未覆盖

**完整读过**：`stdlib_server.py` 453/453、`db.py` 336/336、`ffmpeg_tools.py` 260/260、`enhancers/__init__.py`、`run_backend.py`、`Backend.cs` 625/625。**按播放相关性精读** `services.py` 约 1400 行（1-520、600-744、1120-1520、1575-1700、1845-2345、2460-2700、2815-3130、3380-3520、3900-4081）；未逐行覆盖：合集/播放列表下载（745-1120）、YouTube cookie（3520-3770）、增强版本树（3770-3900）。`bilibili.py` 读 40-190 + 全文 grep；`downloader.py` 只读函数级结构；`ai_subtitle.py` 读 435-590；`ai_pipeline.py`/`enhancers/*.py` 只审注册与调用契约，未评算法正确性。

**未覆盖**：① 打包/CI（`packaging/`、`setup.iss`）未审；② C++ 引擎（`dsh_engine`）不在范围；③ **未压测**（未启动后端）：文中"最坏 10s+"、"1.4MB"、"8 轮≈24 次请求"均为静态推断，标 **推测（需实测）**；④ 安装版 embeddable Python 的 site-packages 实际内容未验证（按 `requirements-ai.txt` 推断）；⑤ 全程未修改、未启动任何源码或数据。
