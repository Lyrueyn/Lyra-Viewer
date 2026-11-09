# 量化渐变带（banding）与颗粒/纹理（laplacian）的测量脚本。
# 用法: python measure_banding.py <gray_raw_1280x720> [W] [H]
# 依赖 numpy。读 uint8 灰度 raw（1280*720）。
import sys
import numpy as np

def main():
    if len(sys.argv) < 2:
        print("用法: python measure_banding.py <gray.raw> [W H]"); return
    path = sys.argv[1]
    W = int(sys.argv[2]) if len(sys.argv) > 2 else 1280
    H = int(sys.argv[3]) if len(sys.argv) > 3 else 720
    img = np.fromfile(path, dtype=np.uint8, count=W*H).reshape(H, W).astype(np.float64)

    # --- laplacian std：高频内容（颗粒/细节/噪声水平） ---
    # 标准 4 邻点拉普拉斯：-4*中心 + 上下左右；频响峰在中/高频，能反映颗粒与 banding 阶跃
    lap = np.zeros_like(img)
    lap[1:-1, 1:-1] = (img[:-2, 1:-1] + img[2:, 1:-1] + img[1:-1, :-2]
                       + img[1:-1, 2:] - 4*img[1:-1, 1:-1])
    lap_std = float(np.std(lap))

    # --- banding%：沿 x 的灰度剖面，检测平台（连续等值列）与阶跃 ---
    # 行平均，降低噪声；得到每列亮度 profile（W 个值）
    prof = img.mean(axis=0)   # (W,)
    # 量化到 8bit 台阶（round 到 0..255）
    q = np.round(prof).astype(np.int64)
    # 平台：相邻两列等值 → 该列为平台成员
    same = (q[1:] == q[:-1])
    # 平台长度统计：以游程长度编码，看连续同值段
    # banding% = 平台列数 / 总列数（处于"渐变带"而非平滑变化）
    plateau_cols = int(same.sum())
    banding_pct = 100.0 * plateau_cols / (W - 1)

    # 阶跃计数：相邻列值差的绝对值和（越大越陡/越不平滑）
    d = np.abs(np.diff(prof))
    avg_step = float(np.mean(d))
    max_step = float(np.max(d))

    print(f"laplacian_std={lap_std:.4f}  banding%={banding_pct:.2f}  avg_step={avg_step:.3f}  max_step={max_step:.1f}")

if __name__ == "__main__":
    main()
