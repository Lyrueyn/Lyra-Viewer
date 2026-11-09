import json
W = json.load(open(r"D:\DSH\local-media-player\fsrcnnx_weights.json"))
tot = 0
for i, p in enumerate(W):
    wc = 0
    for t in p["taps"]:
        wc += 16 if t["shape"] == [4, 4] else len(t["w"])
    tot += wc
    print(f"pass {i:2d} {p['desc']:24s} taps={len(p['taps']):2d} weights={wc}")
print("TOTAL weights =", tot)
import os
print("json size =", os.path.getsize(r"D:\DSH\local-media-player\fsrcnnx_weights.json"), "bytes")
