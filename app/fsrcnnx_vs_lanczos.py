# 对比 CPU 完整14层 FSRCNNX 2x vs Lanczos/双三次 放大，量化细节增益。
# luma128.raw (128x128) → 256x256。用 scipy 或 numpy 手写 Lanczos。
import numpy as np
from PIL import Image

def read_pgm(p):
    with open(p,'rb') as f: data=f.read()
    idx=0; parts=[]
    while len(parts)<4:
        while idx<len(data) and data[idx] in b' \t\n\r': idx+=1
        if data[idx:idx+1]==b'#':
            while idx<len(data) and data[idx]!=b'\n': idx+=1
            continue
        tok=b''
        while idx<len(data) and data[idx] not in b' \t\n\r': tok+=data[idx:idx+1]; idx+=1
        parts.append(tok)
    w,h=int(parts[1]),int(parts[2]); idx+=1
    return np.frombuffer(data[idx:idx+w*h],dtype=np.uint8).reshape(h,w).astype(np.float32)

luma = np.fromfile(r'D:\DSH\local-media-player\luma128.raw', dtype=np.uint8).astype(np.float32).reshape(128,128)
fsrcnnx = read_pgm(r'D:\DSH\local-media-player\fsrcnnx_ref_2x.pgm')  # 256x256, 完整14层归一化

def lanczos2x(a, radius=3):
    # 每输出像素映射回源坐标，Lanczos3 插值
    H,W=a.shape; outH,outW=H*2,W*2
    out=np.zeros((outH,outW),dtype=np.float32)
    def sinc(x):
        if abs(x)<1e-6: return 1.0
        px=np.pi*x
        return np.sin(px)/px
    def lz(x,r):
        if abs(x)<1e-6: return 1.0
        return sinc(x)*sinc(x/r)
    for oy in range(outH):
        sy=(oy+0.5)/2.0-0.5   # 目标→源
        cy=np.floor(sy)+0.5
        fy=sy-(cy-0.5)
        for ox in range(outW):
            sx=(ox+0.5)/2.0-0.5
            cx=np.floor(sx)+0.5
            fx=sx-(cx-0.5)
            acc=0.0; wsum=0.0
            for dy in range(-radius+1,radius+1):
                wy=lz(fy-dy,radius)
                iy=int(cy+dy)
                if iy<0 or iy>=H: continue
                for dx in range(-radius+1,radius+1):
                    wx=lz(fx-dx,radius)
                    ix=int(cx+dx)
                    if ix<0 or ix>=W: continue
                    w=wx*wy
                    acc+=a[iy,ix]*w; wsum+=w
            out[oy,ox]=acc/(wsum+1e-6)
    return out

lanc = lanczos2x(luma)
# 双三次简单 (最近邻2x 作对照)
nn2x = np.repeat(np.repeat(luma,2,axis=0),2,axis=1)

def sobel(y):
    g=np.zeros_like(y)
    g[1:-1,1:-1]=np.sqrt((y[1:-1,2:]-y[1:-1,:-2])**2+(y[2:,1:-1]-y[:-2,1:-1])**2)
    return g

for name,img in [('FSRCNNX 14层',fsrcnnx),('Lanczos 2x',lanc),('最近邻 2x',nn2x)]:
    g=sobel(img)
    rows=img.mean(axis=1); adj=np.abs(np.diff(rows)).mean()
    print(f'{name}: mean={img.mean():.1f} std={img.std():.1f} Sobel={g.mean():.4f} 行adj={adj:.3f}')
# 归一化到 0-255 对比 Sobel（FSRCNNX 已归一化，Lanczos/最近邻未归一化）
def norm(a): return (a-a.min())/(a.max()-a.min()+1e-6)*255
for name,img in [('FSRCNNX 14层',fsrcnnx),('Lanczos 2x',lanc),('最近邻 2x',nn2x)]:
    g=sobel(norm(img))
    print(f'归一化后 {name}: Sobel={g.mean():.4f}')
