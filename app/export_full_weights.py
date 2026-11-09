# export_full_weights.py：把 fsrcnnx_weights.json 14 个 pass 的权重按序打成单一 storage buffer，
# 并写出 fsr_full_weights.f4（浮点）与 fsr_passes.json（每个 pass 的偏移/输入绑定/输出/leaky 元数据）。
# GPU 侧每个 pass 用统一偏移读 params，做通用 4ch 卷积 + PReLU。
import json, struct, numpy as np

d = json.load(open(r'D:\DSH\local-media-player\fsrcnnx_weights.json'))

# 目标：把每个 pass 打包成 [bias4 | taps权重 | leaky1] 连续段，记录每段起始偏移。
# 约定每个 pass 的权重布局（flat，float）：
#   [0..3]       bias(4)
#   [4..]        taps 权重，按 tap 顺序平铺 (mat4=16 / vec4=4 按 tap 依次)
#   [last]       leaky(1)
# 用 map/feat shader 的读法：bias 在 passStart+0..3，leaky 在 passStart+passLen-1，
#   权重从 passStart+4 起（taps 数量由 pass 元数据给出）。
floats = []
passes = []
cur = 0
for i, p in enumerate(d):
    bias = np.asarray(p['bias'], dtype=np.float32)          # 4
    leaky = np.asarray(p.get('leaky', 1.0), dtype=np.float32).flatten()   # 1 或 4 分量
    seg = [float(x) for x in bias]
    # 每个 tap：权重（mat4=16 或 vec4=4）
    taps_meta = []
    for t in p['taps']:
        w = np.asarray(t['w'], dtype=np.float32).flatten().tolist()
        seg += w
        taps_meta.append({'in': t['in'], 'ox': t['ox'], 'oy': t['oy'],
                          'shape': t['shape'], 'len': len(w)})
    # leaky：1 分量则广播成 4（每通道），4 分量直接写
    lk = [float(x) for x in leaky]
    if len(lk) == 1:
        lk = lk * 4
    leaky_off = cur + len(seg)
    seg += lk                                # 4 分量 leaky 连续
    offset = cur
    passes.append({'idx': i, 'save': p.get('save'), 'offset': offset,
                   'len': len(seg), 'bias_off': offset, 'leaky_off': leaky_off,
                   'taps': taps_meta, 'binds': p['binds'], 'leaky': lk})
    floats += seg
    cur += len(seg)

floats = np.asarray(floats, dtype=np.float32)
floats.tofile(r'D:\DSH\local-media-player\fsr_full_weights.f4')
json.dump(passes, open(r'D:\DSH\local-media-player\fsr_passes.json', 'w'))
print('total floats:', len(floats))
print('passes:', len(passes))
for pp in passes:
    print(f"  p{pp['idx']} save={pp['save']} offset={pp['offset']} len={pp['len']} taps={len(pp['taps'])} binds={pp['binds']}")
