# dl_hdr_bv.py：查 B 站视频 formats 里是否有 HDR 流（直接带 cookie 用 yt-dlp）。
# 用法: backend\.venv\Scripts\python.exe dl_hdr_bv.py BV1R1e4zKEh1
import sys
sys.path.insert(0, r"D:\DSH\local-media-player\backend")
import yt_dlp  # noqa: E402
from app import services  # noqa: E402

BVID = sys.argv[1] if len(sys.argv) > 1 else "BV1R1e4zKEh1"
url = f"https://www.bilibili.com/video/{BVID}"
services.import_cookie_from_playwright()
cookie = services.get_cookie()
print("cookie len:", len(cookie))

opts = {"skip_download": True, "http_headers": {"Cookie": cookie}}
with yt_dlp.YoutubeDL(opts) as ydl:
    info = ydl.extract_info(url, download=False)

print("title =", info.get("title"))
print("=" * 60)
for f in info.get("formats", []):
    if f.get("vcodec") == "none" and f.get("acodec") == "none":
        continue
    note = (f.get("format_note") or "")
    vc = (f.get("vcodec") or "")
    fid = str(f.get("format_id")); h = f.get("height")
    low = (note + " " + vc + " " + fid).lower()
    tag = []
    if "hdr" in low: tag.append("HDR")
    if "dolby" in low: tag.append("DOLBY")
    if "av1" in low: tag.append("AV1")
    if "dvh" in low: tag.append("DV")
    if "p010" in low or "10" in vc: tag.append("10BIT")
    if "color_primaries" in str(f.get("color_primaries")): tag.append("CP")
    print(f"  id={fid} h={h} vcodec={vc[:26]:26s} note={note[:16]:16s} {' '.join(tag)}")
