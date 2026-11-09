"""验证选流器修复：把 P1 的每一路 DASH 流都 ffprobe 一遍，再看各档请求选中哪一路。

用法：cd backend && .venv\\Scripts\\python.exe tools\\verify_stream_pick.py
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import bilibili, services  # noqa: E402

BVID, CID = "BV1Sf4y1q7H9", 240681376
FFPROBE = r"D:\DSH\VOCALIVE\ffmpeg\bin\ffprobe.exe"
bilibili.set_login_cookie(services.get_cookie())


def playurl(qn: int) -> dict:
    params = {"bvid": BVID, "cid": CID, "qn": qn, "fnval": 4048, "fourk": 1}
    params.update(bilibili._dm_params())
    return json.loads(bilibili._http_get(bilibili._wbi_url(
        "https://api.bilibili.com/x/player/wbi/playurl", params)).decode("utf-8"))


def probe(url: str) -> dict:
    hdrs = "".join("%s: %s\r\n" % (k, v) for k, v in bilibili._headers().items())
    try:
        p = subprocess.run([FFPROBE, "-v", "error", "-headers", hdrs, "-select_streams", "v:0",
                            "-show_entries",
                            "stream=width,height,pix_fmt,color_transfer,color_primaries,codec_name",
                            "-of", "json", url], capture_output=True, text=True, timeout=40)
        return json.loads(p.stdout)["streams"][0] if p.stdout.strip() else {"err": p.stderr[:60]}
    except Exception as e:
        return {"err": str(e)[:60]}


def tag(s: dict) -> str:
    if "err" in s:
        return "探测失败(%s)" % s["err"]
    ct = s.get("color_transfer")
    hdr = "HDR10(PQ)" if ct == "smpte2084" else ("HLG" if ct == "arib-std-b67" else "SDR")
    return "%s %sx%s %s %s" % (s.get("codec_name"), s.get("width"), s.get("height"),
                               s.get("pix_fmt"), hdr)


d0 = playurl(125)
videos = ((d0.get("data") or {}).get("dash") or {}).get("video") or []
print("=== P1 每一路流的真实参数（ffprobe 直读流 URL，不下载）===")
seen = set()
for v in sorted(videos, key=lambda x: -(x.get("height") or 0)):
    sid = v.get("id")
    if sid in seen:
        continue
    seen.add(sid)
    print("  id=%-4s height=%-5s codecid=%-4s bandwidth=%-9s → %s"
          % (sid, v.get("height"), v.get("codecid"), v.get("bandwidth"),
             tag(probe(v.get("baseUrl") or v.get("base_url") or ""))))

print()
print("=== 修复后：各档请求选中哪一路（应严格对应档位）===")
for qn, name in ((125, "杜比视界"), (120, "4K"), (112, "1080P+"), (80, "1080P"), (64, "720P"), (32, "480P")):
    try:
        s = bilibili.get_playurl(BVID, CID, qn=qn, hi_res=False)
        print("  请求 qn=%-4s(%-6s) → 选中 quality=%-4s(%s) height=%-5s"
              % (qn, name, s["quality"], bilibili.QUALITY_NAMES.get(s["quality"], "?"), s["height"]))
    except Exception as e:
        print("  请求 qn=%-4s(%-6s) → 失败 %s" % (qn, name, e))
