# download_hdr.py：下载 B 站视频的 4K HDR(HEVC codecid=12) 流 + 音频，用 ffmpeg 合流。
# 用法: backend\.venv\Scripts\python.exe download_hdr.py BV1R1e4zKEh1 <out.mp4>
import sys, json, subprocess, os
sys.path.insert(0, r"D:\DSH\local-media-player\backend")
from app import bilibili, services  # noqa: E402
services.import_cookie_from_playwright()
bilibili.set_login_cookie(services.get_cookie())

BVID = sys.argv[1] if len(sys.argv) > 1 else "BV1R1e4zKEh1"
OUT = sys.argv[2] if len(sys.argv) > 2 else r"D:\DSH\local-media-player\bili_4khdr.mp4"
FFMPEG = r"D:\ETHEREIS\third_party\ffmpeg\bin\ffmpeg.exe"

info = bilibili.get_video_info(BVID)
cid = info["cid"]
params = {"bvid": BVID, "cid": cid, "qn": 120, "fnval": 4048, "fourk": 1}
params.update(bilibili._dm_params())
url = bilibili._wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
data = json.loads(bilibili._http_get(url).decode("utf-8")) or {}
if data.get("code") != 0:
    print("playurl code=", data.get("code"), data.get("message")); sys.exit(1)
d = data.get("data") or {}; dash = d.get("dash") or {}

# 选 HDR 视频流：优先 codecid=12(HEVC 10bit) 的最高分辨率；其次 codecid=13(AV1 10bit)
videos = dash.get("video") or []
def is_hdr(v):
    # codecid 12=HEVC 10bit, 13=AV1 10bit；B站 HDR/杜比用这两种
    return v.get("codecid") in (12, 13)
hdr = [v for v in videos if is_hdr(v)]
if not hdr:
    print("未找到 HDR 流，回退最高清"); hdr = videos
hdr.sort(key=lambda v: -(v.get("height") or 0))
vbest = hdr[0]
print("选 HDR 视频流: qn=%s codecid=%s %sx%s codecs=%s" % (
    vbest.get("id"), vbest.get("codecid"), vbest.get("width"), vbest.get("height"), vbest.get("codecs")))
vurl = vbest.get("baseUrl") or vbest.get("base_url") or ""

# 音频：优先 Hi-Res FLAC，否则第 1 个 AAC
audio_url = ""
audios = dash.get("audio") or []
if audios:
    audio_url = audios[0].get("baseUrl") or audios[0].get("base_url") or ""
    print("选音频: codecs=%s" % (audios[0].get("codecs"),))

# 用 ffmpeg 直接拉流（B站防盗链需 Referer/UA/Cookie），忽略不必要进度
headers = bilibili._headers()
ref = "https://www.bilibili.com"
cmd = [FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
       "-headers", f"Referer: {ref}\r\nUser-Agent: {headers.get('User-Agent')}\r\nCookie: {services.get_cookie()}\r\n",
       "-i", vurl]
if audio_url:
    cmd += ["-i", audio_url, "-map", "0:v:0", "-map", "1:a:0"]
cmd += ["-c:v", "copy", "-c:a", "aac", "-movflags", "+faststart", OUT]
print("ffmpeg 合流中 ->", OUT)
r = subprocess.run(cmd, capture_output=True, text=True)
print("exit =", r.returncode)
if r.returncode != 0:
    print("STDERR:\n", r.stderr[-1500:])
else:
    sz = os.path.getsize(OUT)
    print("OK 文件大小 = %.1f MiB" % (sz / 1048576))
