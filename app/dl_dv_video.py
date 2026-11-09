import sys, json, subprocess, os
sys.path.insert(0, r"D:\DSH\local-media-player\backend")
from app import bilibili, services
services.import_cookie_from_playwright()
bilibili.set_login_cookie(services.get_cookie())
BVID="BV1Sf4y1q7H9"; OUT=r"D:\DSH\local-media-player\dv_video.mp4"
FFMPEG=r"D:\ETHEREIS\third_party\ffmpeg\bin\ffmpeg.exe"
info=bilibili.get_video_info(BVID); cid=info["cid"]
params={"bvid":BVID,"cid":cid,"qn":120,"fnval":4048,"fourk":1}
params.update(bilibili._dm_params())
url=bilibili._wbi_url("https://api.bilibili.com/x/player/wbi/playurl", params)
data=json.loads(bilibili._http_get(url).decode("utf-8")) or {}
d=data.get("data",{}); dash=d.get("dash",{})
vids=dash.get("video",[])
hdr=[v for v in vids if v.get("codecid") in (12,13)]
hdr.sort(key=lambda v:-(v.get("height") or 0))
vb=hdr[0]
print("视频流:", vb.get("width"),"x",vb.get("height"), vb.get("codecs"))
vurl=vb.get("baseUrl") or vb.get("base_url") or ""
headers=bilibili._headers(); ref="https://www.bilibili.com"
cmd=[FFMPEG,"-hide_banner","-loglevel","error","-y",
 "-headers",f"Referer: {ref}\r\nUser-Agent: {headers.get('User-Agent')}\r\nCookie: {services.get_cookie()}\r\n",
 "-i",vurl, "-c:v","copy","-an","-movflags","+faststart",OUT]
print("拉视频流...")
r=subprocess.run(cmd,capture_output=True,text=True)
print("exit=",r.returncode)
if r.returncode!=0: print("STDERR:\n",r.stderr[-1500:])
else: print("OK", round(os.path.getsize(OUT)/1048576,1),"MiB")
