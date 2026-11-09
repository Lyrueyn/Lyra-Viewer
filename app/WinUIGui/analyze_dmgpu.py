from PIL import Image
import numpy as np
img=Image.open(r"F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64\dm_gpu.png").convert('RGB')
a=np.array(img).astype(int)
W=img.width
lum=a.sum(axis=2)
bright=(lum>280)
print("dm_gpu(弹幕纹理) 顶部 y0..200 分析 (亮阈值280):")
fullrows=[]
for y in range(0,200):
    n=int(bright[y].sum())
    if n> W*0.6: fullrows.append(y)
    if y%5==0 and n>0:
        xs=np.where(bright[y])[0]
        print(f" y={y}: 亮px={n}  x范围 {xs.min()}..{xs.max()} 占宽比 {n/W:.2f}")
print("满宽(>60%)亮行 y:", (fullrows[0],fullrows[-1]) if fullrows else "无")
print("顶部 y0..200 亮px 总数:", int(bright[:200].sum()))
