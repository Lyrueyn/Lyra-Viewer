"""对比"请求 HDR（杜比视界/4K）"与"请求普通 720P"到底差在哪。

不下载整个文件：对 B站返回的每一路 DASH 流的 URL 直接用 ffprobe 读首部，
拿到**真实** width/height/pix_fmt/color_transfer（PQ=HDR10）/color_primaries。

用法：cd backend && .venv\\Scripts\\python.exe tools\\cmp_hdr_vs_720p.py
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

BVID = "BV1Sf4y1q7H9"
FFPROBE = r"D:\DSH\VOCALIVE\ffmpeg\bin\ffprobe.exe"

bilibili.set_login_cookie(services.get_cookie())
info = bilibili.get_video_info(BVID)
CID = info["pages"][0]["cid"]
print("视频《%s》 P1 cid=%s  B站弹幕总数=%s\n" % (info["title"][:40], CID, (info.get("stat") or {}).get("danmaku")))


def playurl(qn: int, fnval: int) -> dict:
    params = {"bvid": BVID, "cid": CID, "qn": qn, "fnval": fnval, "fourk": 1}
    params.update(bilibili._dm_params())
    url = bilibili._wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
    return json.loads(bilibili._http_get(url).decode("utf-8"))


def probe(url: str) -> dict:
    """ffprobe 只读首部（不下载整段），返回真实编码参数。"""
    hdrs = "".join("%s: %s\r\n" % (k, v) for k, v in bilibili._headers().items())
    try:
        p = subprocess.run(
            [FFPROBE, "-v", "error", "-headers", hdrs, "-select_streams", "v:0",
             "-show_entries", "stream=width,height,pix_fmt,color_transfer,color_primaries,codec_name",
             "-of", "json", url],
            capture_output=True, text=True, timeout=40)
        if p.returncode != 0 or not p.stdout.strip():
            return {"err": (p.stderr or "no output").strip()[:80]}
        return json.loads(p.stdout)["streams"][0]
    except Exception as e:
        return {"err": "%s: %s" % (type(e).__name__, str(e)[:60])}


def hdr_tag(s: dict) -> str:
    if "err" in s:
        return "探测失败"
    ct, cp = s.get("color_transfer"), s.get("color_primaries")
    if ct == "smpte2084":
        return "HDR10(PQ)"
    if ct == "arib-std-b67":
        return "HLG"
    if ct == "bt709" or ct is None:
        return "SDR"
    return str(ct)


for fnval in (4048, 4052):     # 4048=当前用的；4052 = 4048 | 4（再打开 B站文档里的 HDR 位）
    print("=" * 100)
    print("fnval=%d  %s" % (fnval, "（当前代码用的）" if fnval == 4048 else "（额外打开 HDR 标志位 bit4=4）"))
    print("=" * 100)
    for qn, name in ((125, "杜比视界"), (120, "4K"), (112, "1080P+"), (80, "1080P"), (64, "720P"), (32, "480P")):
        try:
            d = playurl(qn, fnval)
        except Exception as e:
            print("  请求 qn=%-4s(%s) → API 失败 %s" % (qn, name, e))
            continue
        if d.get("code") != 0:
            print("  请求 qn=%-4s(%s) → code=%s %s" % (qn, name, d.get("code"), d.get("message")))
            continue
        data = d.get("data") or {}
        dash = data.get("dash") or {}
        videos = dash.get("video") or []
        q = data.get("quality")
        # 我们的选择逻辑：取不高于 _QN_HEIGHT[qn] 的最高流
        picked = bilibili._pick_video_stream(videos, bilibili._QN_HEIGHT.get(qn, 2160))
        avail = sorted({(v.get("id"), v.get("height")) for v in videos}, key=lambda x: -(x[1] or 0))
        print("  请求 qn=%-4s(%-6s) → B站报 quality=%-4s(%s)  可选流(id,height)=%s"
              % (qn, name, q, bilibili.QUALITY_NAMES.get(q, "?"), avail))
        if picked:
            url = picked.get("baseUrl") or picked.get("base_url") or ""
            pr = probe(url)
            print("        我们选中的流: id=%-5s height=%-5s codecid=%-4s | 实测 %s %sx%s %s → %s"
                  % (picked.get("id"), picked.get("height"), picked.get("codecid"),
                     pr.get("codec_name"), pr.get("width"), pr.get("height"),
                     pr.get("pix_fmt"), hdr_tag(pr)))
    print()
