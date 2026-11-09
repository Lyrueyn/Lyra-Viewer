# list_hdr_dash.py：直接查 B 站 DASH 视频流的 codecid/HDR 标志，定位 HDR 流。
# 用法: backend\.venv\Scripts\python.exe list_hdr_dash.py BV1R1e4zKEh1
import sys, json
sys.path.insert(0, r"D:\DSH\local-media-player\backend")
from app import bilibili, services  # noqa: E402
services.import_cookie_from_playwright()   # 确保登录态注入
bilibili.set_login_cookie(services.get_cookie())

BVID = sys.argv[1] if len(sys.argv) > 1 else "BV1R1e4zKEh1"
info = bilibili.get_video_info(BVID)
cid = info["cid"]
print("title =", info["title"], "cid =", cid)

params = {"bvid": BVID, "cid": cid, "qn": 120, "fnval": 4048, "fourk": 1}
params.update(bilibili._dm_params())
url = bilibili._wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
data = json.loads(bilibili._http_get(url).decode("utf-8")) or {}
if data.get("code") != 0:
    print("playurl code=", data.get("code"), data.get("message")); sys.exit(1)
d = data.get("data") or {}
dash = d.get("dash") or {}
print("quality =", d.get("quality"), "fnval=", d.get("fnval"))
print("=" * 70)
print("--- 视频流 (含 codecid / HDR 元数据) ---")
for v in dash.get("video") or []:
    codecid = v.get("codecid"); h = v.get("height"); q = v.get("id"); w=v.get("width")
    # 用 ffprobe 无法在此判断，但 codecid=12 是 HEVC HDR；color_primaries 若出现在 side 里
    marker = "HDR?" if codecid == 12 else ("AV1" if codecid == 13 else "SDR/AVC")
    print(f"  qn={q} codecid={codecid} {w}x{h} width={w} "
          f"bac={v.get('codecs')} {marker}")
print("--- 音频流 ---")
for a in dash.get("audio") or []:
    print(f"  id={a.get('id')} codecs={a.get('codecs')} ch={a.get('channels')} srate={a.get('sampling_rate')}")
flac = dash.get("flac")
if flac: print("--- FLAC(Hi-Res):", bool(flac.get('audio')))
