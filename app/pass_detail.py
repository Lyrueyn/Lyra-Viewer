import json
d = json.load(open(r'D:\DSH\local-media-player\fsrcnnx_weights.json'))
# 看每个 pass 的 desc + binds + save + taps 细节
for i, p in enumerate(d):
    print(f"=== pass{i} desc={p.get('desc')} save={p.get('save')} binds={p.get('binds')} leaky={p.get('leaky')}")
    for j, t in enumerate(p.get('taps', [])):
        shape = t.get('shape')
        print(f"    tap{j}: in={t['in']}({p['binds'][t['in']]}) off=({t['ox']},{t['oy']}) shape={shape}")
    print()
