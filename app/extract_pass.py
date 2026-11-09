# 从 fsr_full_weights.f4 提取指定 pass（用 fsr_passes.json offset/len）为独立 .f4 文件。
import json, numpy as np
d = json.load(open(r'D:\DSH\local-media-player\fsr_passes.json'))
fw = np.fromfile(r'D:\DSH\local-media-player\fsr_full_weights.f4', dtype=np.float32)
for pp in d:
    if pp['save'] in ('SUBCONV1', 'FEATURE1'):
        seg = fw[pp['offset']:pp['offset']+pp['len']]
        name = 'sub_weights.f4' if pp['save']=='SUBCONV1' else 'feat_weights.f4'
        seg.tofile(rf'D:\DSH\local-media-player\{name}')
        print(f"导出 {name}  len={pp['len']}  (save={pp['save']})")
