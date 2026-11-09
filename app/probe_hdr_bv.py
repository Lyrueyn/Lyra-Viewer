# probe_hdr_bv.py：查 B 站视频的 HDR 信息（用歌单器的登录态）。
# 用法: backend\.venv\Scripts\python.exe probe_hdr_bv.py BV1R1e4zKEh1
import sys
from pathlib import Path
BACKEND = Path(__file__).resolve().parent.parent / "backend"
sys.path.insert(0, str(BACKEND))
from app import bilibili  # noqa: E402

BVID = sys.argv[1] if len(sys.argv) > 1 else "BV1R1e4zKEh1"

info = bilibili.get_video_info(BVID)
print("title   =", info["title"])
print("cid     =", info["cid"], "aid =", info["aid"], "duration =", info["duration"])
cid = info["cid"]
print("=" * 60)
try:
    quals = bilibili.get_available_qualities(BVID, cid)
    print("available qualities (fnval=4048, fourk=1):", len(quals), "档")
    for q in quals:
        print("   ", q)
except Exception as e:
    import traceback; traceback.print_exc()
