# fsrcnnx_parser.py：把 FSRCNNX mpv 钩子 glsl 解析成"逐 pass 卷积权重"结构化数据。
# 每个 pass 的 hook() 形如：
#   res = bias(vec4)
#   res += mat4(w16) * SRC_texOff(off)   # 对每个输入绑定 SRC 在 3x3 邻域 off 处
#   res = max(res, 0) + leaky*min(res,0)  # PReLU
# 输出：每 pass = { binds: [src名...], taps: [ [inIdx, off, 4x4矩阵(list16)] ], bias: vec4, leaky: float }
# 用 numpy 可复现任意输入的该 pass 输出（用于验证）。
import re, json, struct
import numpy as np

def parse_pass(block):
    desc = re.search(r'//!DESC (.+)', block).group(1)
    binds = re.findall(r'//!BIND (\w+)', block)
    save = re.search(r'//!SAVE (\w+)', block)
    save = save.group(1) if save else None
    # 逐行提取 res = bias / res += mat4*texOff
    taps = []
    bias = np.zeros(4, dtype=np.float32)
    leaky = 1.0
    for line in block.splitlines():
        m = re.search(r'res = vec4\(([^)]*)\)', line)
        if m:
            bias = np.array([float(x) for x in m.group(1).split(',')], dtype=np.float32)
        # PReLU：leaky 是 vec4（每通道斜率不同），完整提取 4 分量；若只有一个则复制成 4。
        # 匹配 res = max(res, vec4(0.0)) + vec4(l0,l1,l2,l3) * min(res, vec4(0.0));
        mm = re.search(r'res = max\(res, vec4\(0\.0\)\) \+ vec4\(([^)]*)\) \* min\(res, vec4\(0\.0\)\)', line)
        if mm:
            vals = [float(x) for x in mm.group(1).split(',')]
            if len(vals) == 4:
                leaky = np.array(vals, dtype=np.float32)
            elif len(vals) == 1:
                leaky = np.array([vals[0]]*4, dtype=np.float32)
            else:
                leaky = np.array(vals + vals[:4-len(vals)], dtype=np.float32)
        # 精确分离：找 _texOff( ... ) 的完整参数（处理嵌套 vec2 需要括号平衡）
        tmm = re.search(r'(\w+)_texOff\((.*)\)\s*;?$', line)
        if tmm:
            srcname = tmm.group(1)
            arg = tmm.group(2)
            # 从 line 里 "res += <加权部分>" 里提取权重（* 左侧，兼容 float(...) 包裹）
            wpart = None
            wm = re.search(r'res \+= (.*?)' + re.escape(srcname) + r'_texOff', line)
            if wm:
                wpart = wm.group(1).rstrip('* ').strip()
                # 去掉可能的 float( 前缀包装
                wpart = wpart.replace('float(', '').rstrip()
            om = re.search(r'vec2\((-?\d+),\s*(-?\d+)\)', arg)
            ox = int(om.group(1)) if om else 0; oy = int(om.group(2)) if om else 0
            if wpart and ('mat4' in wpart or 'vec4' in wpart):
                mn = re.search(r'mat4(?:x4)?\(([^)]*)\)', line)
                vn = re.search(r'vec4\(([^)]*)\)', line)
                if mn:
                    w = np.array([float(x) for x in mn.group(1).split(',')], dtype=np.float32).reshape(4,4,order='F')
                    taps.append((binds.index(srcname), ox, oy, w))
                elif vn:
                    w = np.array([float(x) for x in vn.group(1).split(',')], dtype=np.float32)
                    taps.append((binds.index(srcname), ox, oy, w))
            else:
                # 纯加特征（res += SRC_texOff(0)）：identity tap
                taps.append((binds.index(srcname), ox, oy, np.eye(4, dtype=np.float32)))
        # 另：res += mat4/w * NAME_texOff 但行尾可能无 ; （mapping 用 ";"，featmap 也 ";"）
    return {"desc": desc, "binds": binds, "save": save, "bias": bias, "taps": taps, "leaky": leaky}

def parse_all(path):
    src = open(path).read()
    blocks = re.split(r'//!HOOK', src)[1:]
    passes = []
    for b in blocks:
        if 'hook()' not in b:
            continue
        passes.append(parse_pass(b))
    return passes

if __name__ == "__main__":
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:\DSH\local-media-player\FSRCNNX_x2_8-0-4-1.glsl"
    passes = parse_all(path)
    print("pass 数 =", len(passes))
    for i, p in enumerate(passes):
        lk = np.asarray(p["leaky"]).flatten()
        lks = ",".join(f"{x:.3f}" for x in lk)
        print(f"p{i}: {p['desc']:24s} binds={p['binds']} taps={len(p['taps'])} leaky=[{lks}]")
    # 存成结构化 json（便于后续生成 Slang 或 CPU 参考）
    # 存成结构化 json（便于后续生成 Slang 或 CPU 参考）
    data = []
    for p in passes:
        tapsj = []
        for t in p["taps"]:
            w = t[3]
            tapsj.append({"in": t[0], "ox": t[1], "oy": t[2], "w": w.flatten(order='C').tolist(),
                          "shape": list(w.shape)})
        lk = p["leaky"]
        if np.isscalar(lk):
            lk = [float(lk)]*4
        else:
            lk = [float(x) for x in np.asarray(lk).flatten()]
        data.append({"desc": p["desc"], "binds": p["binds"], "save": p["save"], "bias": p["bias"].tolist(),
                     "leaky": lk, "taps": tapsj})
    json.dump(data, open(r"D:\DSH\local-media-player\fsrcnnx_weights.json", "w"))
    print("已写出 fsrcnnx_weights.json, pass 数 =", len(data))
    # 打印每 pass tap 的权重形状和邻域范围
    for i, p in enumerate(data):
        if p["taps"]:
            oxs = [t["ox"] for t in p["taps"]]; oys = [t["oy"] for t in p["taps"]]
            shapes = set(tuple(t["shape"]) for t in p["taps"])
            print(f"p{i} {p['desc']:22s} taps={len(p['taps'])} x[{min(oxs)}..{max(oxs)}] y[{min(oys)}..{max(oys)}] shapes={shapes}")
        else:
            print(f"p{i} {p['desc']:22s} taps=0 (纯 bias/重排)")
