"""标准库 HTTP 后端（零第三方依赖，http.server 实现）。

路由与 app/api.py（fastapi 版）一致，全部调用 app/services.py 的服务层。
未安装 fastapi/uvicorn 也能跑；yt-dlp 未装时仅"解析/下载"接口不可用。
启动：python -m app.stdlib_server --port 8765
"""
from __future__ import annotations

import argparse
import json
import logging
import re
import subprocess
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import ai_subtitle, playlists_db, services

logger = logging.getLogger(__name__)


class Handler(BaseHTTPRequestHandler):
    server_version = "LocalMediaPlayer/0.1"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args) -> None:
        logger.debug(fmt, *args)  # 静默请求日志

    # ---- 响应工具 ----
    def _send(self, obj, code: int = 200) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def _send_error(self, code: int, detail: str) -> None:
        self._send({"detail": detail}, code)

    def _read_json(self) -> dict:
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0:
            # ★ 防御：有些客户端（如 .NET 的 PostAsJsonAsync）会用 chunked 传输、不带 Content-Length，
            #   而 BaseHTTPRequestHandler 不会自动解 chunked → body 会静默读成空（字段全变默认值）。
            #   这里至少把情况写进日志，避免以后又出现"参数莫名变成 0"。
            if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
                logger.warning("收到 chunked 请求体（未解析）path=%s → body 为空", self.path)
            return {}
        try:
            return json.loads(self.rfile.read(n).decode("utf-8"))
        except json.JSONDecodeError:
            return {}

    # ---- 路由 ----
    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        q = urllib.parse.parse_qs(parsed.query)
        try:
            if path == "/health":
                self._send(services.health())
            elif path == "/api/paths":
                self._send(services.get_paths())
            elif path == "/api/config/youtube-cookie":
                _f = services.get_youtube_cookie_file()
                _chk = services.verify_youtube_login() if _f else {"logged_in": False}
                self._send({"has_cookie": bool(_f), "count": services.get_youtube_cookie_count(),
                            "logged_in": bool(_chk.get("logged_in")),
                            "verify_error": _chk.get("error", "")})
            elif path == "/api/media/info":
                _pth = (q.get("path") or [""])[0]
                self._send(services.media_info(_pth))
            elif path == "/api/bilibili/parse":
                url = (q.get("url") or [""])[0]
                self._send(downloader_parse(url))
            elif path == "/api/download/tasks":
                self._send(services.list_download_tasks())
            elif path == "/api/videos":
                self._send(services.list_videos())
            elif path == "/api/download/playlist-items":
                self._send(services.submit_playlist_items(
                    body.get("items") or [],
                    str(body.get("playlist_name", "")),
                ))
            elif path == "/api/playlists":
                self._send(playlists_db.list_playlists())
            elif (m := re.fullmatch(r"/api/playlists/(\d+)", path)):
                self._send(playlists_db.playlist_items(int(m.group(1))))
            elif path.startswith("/api/videos/intro"):
                # ★ q 在上面已经解析好了（path 里不含 query，别再拿 path 去 parse）
                self._send(services.video_intro((q.get("bvid") or [""])[0], int((q.get("page") or ["0"])[0])))
            elif path == "/api/config/cookie":
                self._send(services.get_cookie_detail())
            elif path == "/api/config/danmaku-settings":
                self._send(services.get_danmaku_settings())
            elif path == "/api/enhance/deps":
                self._send(services.deps_status())
            elif path == "/api/ai_subtitle/status":
                self._send(ai_subtitle.status())
            elif path == "/api/enhance/tasks":
                self._send(services.list_enhance_tasks())
            elif path == "/api/enhancers":
                self._send(services.list_enhancers())
            elif path == "/api/enhance/versions":
                self._send(services.list_enhanced_versions(
                    (q.get("media_type") or ["video"])[0],
                    int((q.get("media_id") or ["0"])[0]),
                ))
            elif path == "/api/enhance/all":
                self._send(services.list_all_enhanced())
            elif path == "/api/enhance/product_info":
                # ★ 播放增强版时：查这个文件是哪个增强产物 + 它的源视频信息
                self._send(services.product_info((q.get("path") or [""])[0]))
            elif (m := re.fullmatch(r"/api/enhance/chain/(\d+)", path)):
                # ★ T27：增强链血缘（本任务 + 逐级祖先 + 链最底原始视频）
                self._send(services.get_enhance_chain(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/danmaku", path)):
                self._send(services.video_danmaku(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/stats", path)):
                self._send(services.video_stats(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/comments", path)):
                page = int((q.get("page") or ["1"])[0])
                self._send(services.video_comments(int(m.group(1)), page))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/comment-progress", path)):
                self._send(services.get_comment_fetch_progress(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/comments/(\d+)/replies", path)):
                # ★ 必须在 /comments/(\d+) 之前匹配（虽然 patterns 不冲突，放前面更稳）
                self._send(services.comment_replies(int(m.group(1)), int(m.group(2)),
                                                    int((q.get("page") or ["1"])[0])))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/comments/(\d+)", path)):
                self._send(services.comment_detail(int(m.group(1)), int(m.group(2))))
            else:
                self._send_error(404, "not found")
        except ValueError as e:
            self._send_error(404, str(e))
        except RuntimeError as e:
            self._send_error(400, str(e))
        except Exception as e:
            logger.exception("GET %s 失败", path)
            self._send_error(500, str(e))

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        body = self._read_json()
        try:
            if path == "/api/download/submit":
                self._send(services.submit_download(
                    str(body.get("url", "")),
                    str(body.get("format_sel", "bv*+ba/b")),
                    int(body.get("qn", 120)),
                    bool(body.get("hi_res", True)),   # 是否优选 Hi-Res 无损音频
                    int(body.get("page", 0)),         # 分P序号（0=第一P）
                ))
            elif path == "/api/config/youtube-cookie":
                self._send(services.set_youtube_cookie(body))
            elif path == "/api/download/collection":
                self._send(services.submit_collection_download(
                    str(body.get("url", "")),
                    str(body.get("format_sel", "bv*+ba/b")),
                    int(body.get("qn", 120)),
                    bool(body.get("hi_res", True)),
                ))
            elif path == "/api/download/playlist-items":
                self._send(services.submit_playlist_items(
                    body.get("items") or [],
                    str(body.get("playlist_name", "")),
                ))
            elif path == "/api/playlists":
                self._send(playlists_db.create_playlist(str(body.get("name", ""))))
            elif (m := re.fullmatch(r"/api/playlists/(\d+)/items", path)):
                self._send(playlists_db.add_video_to_playlist(
                    int(m.group(1)), int(body.get("video_id", 0))))
            elif (m := re.fullmatch(r"/api/playlists/(\d+)/items/remove", path)):
                self._send(playlists_db.remove_video_from_playlist(
                    int(m.group(1)), int(body.get("video_id", 0))))
            elif path == "/api/videos/order":
                # ★ 首页拖拽排序：保存卡片顺序
                self._send(services.set_video_order(body.get("keys") or []))
            elif (m := re.fullmatch(r"/api/playlists/(\d+)/order", path)):
                # ★ 合集内拖拽排序：按新顺序重排 position（显示时的 P 几）
                self._send(services.set_playlist_order(int(m.group(1)), body.get("video_ids") or []))
            elif path == "/api/enhance/submit_chain":
                # ★ T27：提交增强链（多节点串行）。1~8 个节点，一条 task 记录 + 节点序列。
                # ★ 2026-10-20：新增 nodes=[{key, params}]（节点图每个节点可带自己的参数）；
                #   enhancers=[key] 老格式继续兼容。
                _nodes = body.get("nodes")
                _enh = body.get("enhancers") or []
                if isinstance(_enh, str):
                    _enh = [_enh]
                try:
                    self._send(services.submit_enhance_chain(
                        int(body.get("media_id", 0) or 0),
                        str(body.get("media_type", "video") or ""),
                        str(body.get("source_path", "") or ""),
                        _enh,
                        str(body.get("device", "cuda")),
                        _nodes,
                    ))
                except ValueError as e:          # 参数问题（空节点/未注册/超过8个）→ 400，而不是 500
                    self._send({"detail": str(e)}, 400)
            elif path == "/api/enhance/deps/install":
                self._send(services.deps_start_install())
            elif path == "/api/enhance/deps/cancel":
                self._send(services.deps_cancel_install())
            # ================= ★ AI 字幕（Whisper）=================
            elif path == "/api/ai_subtitle/deps/install":
                self._send(ai_subtitle.start_deps_install())
            elif path == "/api/ai_subtitle/deps/cancel":
                self._send(ai_subtitle.cancel_deps_install())
            elif path == "/api/ai_subtitle/download":
                _model = str(body.get("model", "") or "")
                self._send(ai_subtitle.start_download(_model))
            elif path == "/api/ai_subtitle/download/cancel":
                self._send(ai_subtitle.cancel_download())
            elif path == "/api/ai_subtitle/select":
                self._send(ai_subtitle.select_model(str(body.get("model", "") or "")))
            elif path == "/api/ai_subtitle/delete":
                self._send(ai_subtitle.delete_model(str(body.get("model", "") or "")))
            elif path == "/api/ai_subtitle/generate":
                self._send(ai_subtitle.start_generate(str(body.get("path", "") or "")))
            elif path == "/api/ai_subtitle/generate/cancel":
                self._send(ai_subtitle.cancel_generate())
            elif path == "/api/enhance/submit":
                self._send(services.submit_enhance(
                    int(body.get("media_id", 0)),
                    str(body.get("media_type", "audio")),
                    str(body.get("enhancer", "")),
                    str(body.get("device", "cuda")),
                    str(body.get("source_path", "")),   # 链式增强：对已增强版本再增强
                ))
            elif (m := re.fullmatch(r"/api/enhance/tasks/(\d+)/cancel", path)):
                self._send(services.cancel_enhance_task(int(m.group(1))))
            elif path.startswith("/api/videos/intro"):
                # ★ q 在上面已经解析好了（path 里不含 query，别再拿 path 去 parse）
                self._send(services.video_intro((q.get("bvid") or [""])[0], int((q.get("page") or ["0"])[0])))
            elif path == "/api/config/cookie":
                self._send(services.set_cookie(str(body.get("cookie", ""))))
            elif path == "/api/config/cookie/import":
                self._send(services.import_cookie_from_playwright())
            elif path == "/api/config/danmaku-settings":
                self._send(services.save_danmaku_settings(dict(body)))
            elif path == "/api/videos/import":
                self._send(services.import_local_video(
                    str(body.get("path", "")),
                    str(body.get("title", "")),
                ))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/refresh", path)):
                self._send(services.refresh_video(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/desc", path)):
                self._send(services.update_video_desc(int(m.group(1)), str(body.get("desc", "") or "")))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/subtitle/redownload", path)):
                self._send(services.backfill_subtitles(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/download/tasks/(\d+)/cancel", path)):
                self._send(services.cancel_download_task(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/(\d+)/export-clip", path)):
                self._send(services.export_clip(
                    int(m.group(1)),
                    int(body.get("start_ms", 0)),
                    int(body.get("end_ms", 0)),
                    bool(body.get("reencode", False)),
                ))
            else:
                self._send_error(404, "not found")
        except Exception as e:
            logger.exception("POST %s 失败", path)
            self._send_error(500, str(e))

    def do_DELETE(self) -> None:
        parsed = urllib.parse.urlparse(self.path)   # ★ 需要 query 部分（?page=N）
        path = parsed.path
        try:
            if (m := re.fullmatch(r"/api/playlists/(\d+)", path)):
                self._send(playlists_db.delete_playlist(int(m.group(1))))
            elif path == "/api/download/tasks/clear":
                self._send(services.clear_finished_download_tasks())
            elif (m := re.fullmatch(r"/api/enhance/tasks/(\d+)", path)):
                # ★ T27：有子代 → 409（保住血缘，不删文件）
                _res = services.delete_enhance_task(int(m.group(1)))
                if isinstance(_res, dict) and _res.get("conflict"):
                    self._send({"error": _res.get("error", "不能删除")}, 409)
                else:
                    self._send(_res)
            elif (m := re.fullmatch(r"/api/download/tasks/(\d+)", path)):
                self._send(services.delete_download_task(int(m.group(1))))
            elif (m := re.fullmatch(r"/api/videos/by-bvid/([0-9A-Za-z]+)", path)):
                # ★ 可选 ?page=N：只删该分P（含它的所有画质）；不带则删该 BV 全部分P
                _q = urllib.parse.parse_qs(parsed.query)
                _pg = int((_q.get("page") or ["-1"])[0])
                self._send(services.delete_video_by_bvid(m.group(1), page=_pg))
            elif (m := re.fullmatch(r"/api/videos/(\d+)", path)):
                self._send(services.delete_video(int(m.group(1))))
            else:
                self._send_error(404, "not found")
        except Exception as e:
            logger.exception("DELETE %s 失败", path)
            self._send_error(500, str(e))


def downloader_parse(url: str) -> dict:
    """解析视频链接：B 站走零依赖通道（带 Cookie + fourk=1 能识别 4K/1080p60）；
    其它站点（YouTube 等）走 yt-dlp 通用解析。
    """
    # ★ 2026-10-18：先判断是不是 B 站 —— 不是就交给 yt-dlp（YouTube / 其它站点）
    try:
        from . import services as _svc
        _is_bili = bool(_svc._extract_bvid(url))
    except Exception:
        _is_bili = ("bilibili.com" in (url or "")) or ("b23.tv" in (url or ""))
    if not _is_bili:
        from . import services as _svc2
        try:
            return _svc2.parse_generic(url)
        except Exception as e:
            return {"error": "解析失败：" + str(e), "is_bilibili": False}
    try:
        from . import bilibili, services
        bilibili.set_login_cookie(services.get_cookie())  # 确保登录 Cookie 已加载
        bvid = services._extract_bvid(url)
        info = bilibili.get_video_info(bvid)
        # 多P：URL 若带 ?p=N 则解析该分P（cid），否则取第一P
        pages = info.get("pages") or []
        sel_page = services._extract_page(url)   # B站 1-based
        cid = info["cid"]
        if sel_page > 0 and pages:
            cid = pages[min(sel_page - 1, len(pages) - 1)]["cid"]
        _stream = bilibili.get_playurl(bvid, cid)   # 拿清晰度 + Hi-Res 音轨标记
        result = {
            "title": info["title"],
            "uploader": info["owner"],
            "duration": info["duration"],
            "webpage_url": url,
            "bvid": bvid,
            "is_playlist": False,
            "selected_page": sel_page or (1 if pages else 0),
            "pages": [{"page": p.get("page"), "part": p.get("part"),
                       "cid": p.get("cid"), "duration": p.get("duration")} for p in pages],
            # 所有可用清晰度（供选择，多画质共存）
            "formats": [
                {"format_id": str(q["qn"]), "ext": "mp4", "height": q["height"],
                 "vcodec": "", "acodec": "", "filesize": None, "note": q["note"]}
                for q in bilibili.get_available_qualities(bvid, cid)
            ],
            "quality": _stream.get("quality", 0),
            "hires_available": bool(_stream.get("hi_res", False)),   # 有无 Hi-Res 无损音轨（供下载选项）
        }
        # 已下载检测：★ 2026-10-14 改成**按画质**判断（同视频多画质共存）。
        #   以前只按 (bvid, 分P) 判断，界面提示"已下载"会让用户以为不能再下别的画质。
        #   ★ 分P 取 selected_page：多P 视频不带 ?p= 时默认第 1 P，与下载入库的 page 语义一致
        #     （以前用 sel_page or 0 查 page=0，而入库行是 page=1 → 永远查不到、提示从不出现）。
        page_sel = sel_page or (1 if pages else 0)
        records = services.list_video_records(bvid, page_sel)
        have = {r.get("quality", "") for r in records}
        for f in result["formats"]:
            f["downloaded"] = f.get("note", "") in have
        if records:
            result["already_downloaded"] = True
            result["downloaded_qualities"] = sorted(
                have, key=lambda q: services._QUALITY_RANK.get(q, 0), reverse=True)
            result["existing_path"] = records[0]["local_path"]
            result["existing_size"] = records[0]["file_size"]
        return result
    except Exception as e:
        # 回退：yt-dlp 解析
        try:
            from . import downloader
            return downloader.parse_url(url)
        except Exception as e2:
            raise RuntimeError("解析失败：{}".format(e2 or e))


_QUALITY_NAMES = {
    125: "杜比视界", 120: "4K", 116: "1080P60", 112: "1080P+",
    80: "1080P", 64: "720P", 32: "480P", 16: "360P",
}


def free_port(port: int) -> None:
    """若 port 被本项目残留的后端进程占用，杀掉以释放端口。

    解决"多次测试残留的 run_backend.py / stdlib_server 进程一直霸着 8765，
    导致应用连到旧代码"的问题：每次后端启动时自愈。
    只杀命令行明确含 run_backend.py / stdlib_server / app.stdlib_server 的 python 进程，
    绝不误杀其它程序。
    """
    def _listening_pids(port: int) -> set[str]:
        pids: set[str] = set()
        try:
            out = subprocess.run(["netstat", "-ano", "-p", "tcp"],
                                 capture_output=True, text=True, timeout=10).stdout
        except Exception:
            return pids
        for line in out.splitlines():
            if ":{} ".format(port) in line and "LISTENING" in line.upper():
                m = re.search(r"(\d+)\s*$", line.strip())
                if m:
                    pids.add(m.group(1))
        return pids

    def _is_python(pid: str) -> bool:
        try:
            out = subprocess.run(["tasklist", "/fi", "PID eq {}".format(pid)],
                                 capture_output=True, text=True, timeout=10).stdout
        except Exception:
            return False
        return "python.exe" in out or "pythonw.exe" in out

    markers = ("run_backend.py", "stdlib_server", "app.stdlib_server")
    for pid in _listening_pids(port):
        try:
            cli = subprocess.run(
                ["wmic", "process", "where", "processid={}".format(pid),
                 "get", "commandline", "/format:list"],
                capture_output=True, text=True, timeout=10).stdout
        except Exception:
            cli = ""
        is_ours = any(mk in cli for mk in markers)
        if not is_ours and not cli:
            # wmic 拿不到命令行时兜底：监听我方专用端口的 python 进程基本就是残留后端
            is_ours = _is_python(pid)
        if is_ours:
            try:
                subprocess.run(["taskkill", "/F", "/PID", pid],
                               capture_output=True, timeout=10)
                logger.info("已清理残留后端进程 PID=%s（占用端口 %d）", pid, port)
            except Exception:
                logger.warning("清理残留后端 PID=%s 失败（可手动结束）", pid)
        else:
            logger.info("端口 %d 被非本项目进程 PID=%s 占用，已跳过（请手动处理）", port, pid)


def main() -> None:
    parser = argparse.ArgumentParser(description="本地歌单器后端（标准库版）")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO)
    services.init_dirs()
    services.mark_stale_running_tasks()   # 清掉上一进程异常退出留下的"假 running"下载任务
    free_port(args.port)   # 自愈：先清掉残留旧后端，再绑定，避免连到旧代码
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    logger.info("后端已启动: http://%s:%d", args.host, args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
