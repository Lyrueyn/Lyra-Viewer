# fsrcnnx_ref.py：CPU 端到端参考实现（用 fsrcnnx_weights.json 跑完整 FSRCNNX 2x 超分）。
# 目的：证明提取的权重/拓扑正确 —— 输入小图 → 输出 2x 超分 luma，结果非全黑、有结构。
# 严格按 mpv hook 语义：
#   feature map:  res(4ch) += vec4(w) * LUMA(标量)  @ 5x5 邻域
#   mapping:      res(4ch) += mat4(列主序) * src(4ch) @ 3x3 邻域
#   residual:     res += mat4*src + FEATURE(identity 加)
#   subpixel conv: 输出 SUBCONV1 是 W×H×4，aggregation 用它做 2x 像素重排。
import json, numpy as np

PASSES = json.load(open(r"D:\DSH\local-media-player\fsrcnnx_weights.json"))

def prelu(x, a):
    a = np.asarray(a, dtype=np.float32).flatten()
    if a.size == 1:
        return np.maximum(x, 0) + float(a[0]) * np.minimum(x, 0)
    if a.size == 4:
        return np.maximum(x, 0) + a[None, None, :] * np.minimum(x, 0)
    return np.maximum(x, 0) + a[None, None, :4] * np.minimum(x, 0)

def shift(src, ox, oy):
    """把 src[H,W,C] 按邻域偏移 (ox,oy) 移动，边界补 0。"""
    H, W = src.shape[0], src.shape[1]
    out = np.zeros_like(src, dtype=np.float32)
    ys = slice(max(0, -oy), min(H, H - oy))
    yd = slice(max(0, oy), min(H, H + oy))
    xs = slice(max(0, -ox), min(W, W - ox))
    xd = slice(max(0, ox), min(W, W + ox))
    out[yd, xd, :] = src[ys, xs, :]
    return out

def apply_pass(feats, p, H, W):
    bias = np.asarray(p["bias"], dtype=np.float32)
    out = np.broadcast_to(bias, (H, W, 4)).astype(np.float32).copy()
    for t in p["taps"]:
        src = feats[p["binds"][t["in"]]]
        w = np.asarray(t["w"], dtype=np.float32)
        if t["shape"] == [4, 4]:
            wmat = w.reshape(4, 4, order='F')   # 列主序
        else:
            wmat = w                              # vec4 (4,)
        off = shift(src, t["ox"], t["oy"])
        if wmat.ndim == 2:   # mat4 (4,4) 列主序：out[oc] += Σ_ic src[ic]*w[ic,oc]
            out += off @ wmat
        else:                # vec4 (4,)：out[c] += w[c] * src(单相标量)
            out += off[:, :, 0][:, :, None] * wmat[None, None, :]
    return prelu(out, p["leaky"])

def run(luma, W=64, H=64):
    pass

# ---- 主流程：按 pass 顺序执行，save 命名追踪 ----
def forward(luma):
    Hh, Ww = luma.shape
    feats = {"LUMA": luma.astype(np.float32)[:, :, None]}
    for i, p in enumerate(PASSES):
        # 输入尺寸通常保持 W×H（feature/mapping/residual 同分辨率）
        out = apply_pass(feats, p, Hh, Ww)
        if p["save"]:
            feats[p["save"]] = out
    # 输出在 SUBCONV1（W×H×4）
    sc = feats.get("SUBCONV1")
    return sc

if __name__ == "__main__":
    import numpy as np, os
    # 优先读真实 luma（蛇画面），否则用合成渐变
    for cand in [r"D:\DSH\local-media-player\luma128.raw", r"D:\DSH\local-media-player\luma48.raw"]:
        if os.path.exists(cand):
            d = np.fromfile(cand, dtype="u1").astype(np.float32) / 255.0
            W = H = int(np.sqrt(d.size))
            luma = d.reshape(H, W)
            print("用真实 luma", H, "x", W, "from", os.path.basename(cand))
            break
    else:
        H, W = 48, 48
        yy, xx = np.mgrid[0:H, 0:W]
        luma = (xx / (W - 1) * 0.7 + yy / (H - 1) * 0.3) % 1.0
        luma = np.clip(luma, 0, 1).astype(np.float32)

    subconv = forward(luma)          # (H, W, 4)
    print("SUBCONV1 shape =", subconv.shape)
    out2x = np.zeros((H*2, W*2), dtype=np.float32)
    for oy in range(H*2):
        for ox in range(W*2):
            cy, cx = oy//2, ox//2
            ch = (ox % 2)*2 + (oy % 2)
            out2x[oy, ox] = subconv[cy, cx, min(ch, 3)]
    print("2x 超分 out2x: min=%.3f max=%.3f mean=%.3f" % (out2x.min(), out2x.max(), out2x.mean()))
    # 存输入 + 超分 PGM
    for name, arr in [("fsrcnnx_in", luma), ("fsrcnnx_ref_2x", out2x)]:
        a = np.clip((arr - arr.min()) / (arr.max() - arr.min() + 1e-6), 0, 1)
        with open(r"D:\DSH\local-media-player\%s.pgm" % name, "wb") as f:
            f.write(b"P5\n%d %d\n255\n" % (arr.shape[1], arr.shape[0]))
            f.write((a * 255).astype(np.uint8).tobytes())
    print("已写 fsrcnnx_in.pgm / fsrcnnx_ref_2x.pgm")
