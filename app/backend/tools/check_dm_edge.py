"""量化验证弹幕描边：沿"红字"的左边缘取样，看过渡是不是 白底 → 黑边 → 红字（中间不能有发白的像素）。

用法：py -3.10 tools/check_dm_edge.py <png>
"""
import subprocess
import sys

FFMPEG = r"D:\DSH\VOCALIVE\ffmpeg\bin\ffmpeg.exe"
png = sys.argv[1] if len(sys.argv) > 1 else r"F:\DSH\LocalMediaPlayer-Workspace\backups\local-media-player-backup-20261014\dm_white.png"

raw = subprocess.run([FFMPEG, "-v", "error", "-i", png, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                     capture_output=True).stdout
# 尺寸从文件头猜：1920x1080
W, H = 1920, 1080
assert len(raw) >= W * H * 3, "raw 大小不对：%d" % len(raw)


def px(x, y):
    i = (y * W + x) * 3
    return raw[i], raw[i + 1], raw[i + 2]


# 找红色像素（红字 RED）
xs, ys = [], []
for y in range(0, 400):
    for x in range(700, 1250):
        r, g, b = px(x, y)
        if r > 140 and g < 90 and b < 90:
            xs.append(x)
            ys.append(y)
if not xs:
    print("没找到红字像素")
    sys.exit(1)
x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
print("红字包围盒: x %d~%d  y %d~%d" % (x0, x1, y0, y1))

# 取穿过"红字左侧竖笔画"的一行（往左多取一些，能覆盖 白底→黑边→红字 的过渡）
ymid = (y0 + y1) // 2
print("\n穿过 y=%d 的一行（从白底进入黑边再到红字），值 = (R,G,B)：")
prev = None
bright_inside = 0
for x in range(x0 - 14, x0 + 16):
    r, g, b = px(x, ymid)
    mark = ""
    if prev is not None and prev[0] < 120 and r > 180 and g > 180 and b > 180:
        mark = "   ← ★ 黑边之后又出现亮像素（就是用户说的白圈）"
        bright_inside += 1
    print("  x=%-5d (%3d,%3d,%3d)%s" % (x, r, g, b, mark))
    prev = (r, g, b)

print("\n结论:", "✔ 过渡干净（白底→黑边→红字，没有白圈）" if bright_inside == 0
      else "✘ 仍有 %d 个发白像素" % bright_inside)
