# 测量 10bit 交换链抓回的渐变带：解包 A2B10G10R10_PACK32(格式64) 的 G 通道(10bit)。
# 对比 8bit 输出：banding% 应大幅下降、步骤更细（每级 1/1023 而非 1/255），画面更顺滑。
# 用法: python measure_10b.py <raw.w*H*4> W H
import sys
import numpy as np

def main():
    if len(sys.argv) < 4:
        print("用法: python measure_10b.py <raw> W H"); return
    path, W, H = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    data = np.fromfile(path, dtype=np.uint32, count=W*H).reshape(H, W)
    # A2B10G10R10_PACK32: bits[9:0]=R, [19:10]=G, [29:20]=B, [31:30]=A
    R = (data & 0x3FF).astype(np.float64)
    G = ((data >> 10) & 0x3FF).astype(np.float64)
    B = ((data >> 20) & 0x3FF).astype(np.float64)
    # 渐变是灰度，三通道应相近
    print(f"通道范围 R:{R.min():.0f}-{R.max():.0f} G:{G.min():.0f}-{G.max():.0f} B:{B.min():.0f}-{B.max():.0f}")

    # 用 G 通道（沿 x 的亮度渐变）
    prof = G.mean(axis=0)                      # (W,) 列平均亮度（10bit 0-1023）
    npix = W - 1

    # 8bit 量化视角：把 10bit 重量化到 8bit，看"原 8bit 输出会有多少带"
    q8 = np.round(prof / 1023.0 * 255.0).astype(np.int64)
    banding8 = 100.0 * (q8[1:] == q8[:-1]).sum() / npix
    # 10bit 量化视角：native 10bit 台阶
    q10 = np.round(prof).astype(np.int64)
    banding10 = 100.0 * (q10[1:] == q10[:-1]).sum() / npix

    # laplacian std（10bit 尺度）
    lap = np.zeros_like(G)
    lap[1:-1, 1:-1] = G[:-2, 1:-1] + G[2:, 1:-1] + G[1:-1, :-2] + G[1:-1, 2:] - 4*G[1:-1, 1:-1]
    lap_std = float(np.std(lap))

    d = np.abs(np.diff(prof))
    print(f"banding%(按10bit)= {banding10:.2f}  banding%(重量化8bit)= {banding8:.2f}  "
          f"avg_step(10bit)={float(np.mean(d)):.3f}  lap_std={lap_std:.3f}")

if __name__ == "__main__":
    main()
