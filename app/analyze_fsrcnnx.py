# analyze_fsrcnnx.py：解析 FSRCNNX mpv 钩子 glsl，统计每 pass 权重规模，评估移植难度。
import re, sys

path = sys.argv[1] if len(sys.argv) > 1 else r"D:\DSH\local-media-player\FSRCNNX_x2_8-0-4-1.glsl"
src = open(path).read()

blocks = re.split(r'//!HOOK', src)[1:]
total_w = 0
print("=== 每 pass 结构 ===")
for bi, b in enumerate(blocks):
    desc = re.search(r'//!DESC (.+)', b)
    mat4 = len(re.findall(r'mat4\(', b))
    vec4line = len(re.findall(r'res \+= vec4\(', b))
    resline = len(re.findall(r'^\s*res \+= ', b, re.M))
    bind = re.findall(r'//!BIND (\w+)', b)
    save = re.search(r'//!SAVE (\w+)', b)
    outw = re.search(r'//!WIDTH (.+)', b)
    ncomp = re.search(r'//!COMPONENTS (\d+)', b)
    # 估算权重数：mat4(16 float) 或 vec4(4 float)
    w = mat4*16 + vec4line*4 + resline*4
    total_w += w
    print(f"p{bi}: desc={desc.group(1) if desc else '?'} bind={bind} save={save.group(1) if save else '-'} "
          f"outW={'Y' if outw else '-'} comp={ncomp.group(1) if ncomp else '?'} mat4={mat4} vec4+={vec4line} "
          f"res+={resline} ~weights={w}")

print("总权重估算 =", total_w)
print("pass 数 =", len(blocks))
