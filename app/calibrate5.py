import numpy as np
# 标准 ACES fit (Narkowicz 2015), 输入是"相对白场的线性CMOS值".
def aces(x):
    a=2.51;b=0.03;c=2.43;d=0.59;e=0.14
    return np.clip((x*(a*x+b))/(x*(c*x+d)+e),0,1)
# 相对白场: npl=100nit。 天空495nit -> x=4.95, 暗部山脉6nit -> 0.06, 中100nit -> 1.0
print('ACES 相对白场映射:')
for n,x in [('暗部山脉(6nit, x=0.06)',0.06),('天空(495nit,x=4.95)',4.95),('白场(100nit,x=1.0)',1.0)]:
    o=aces(x); print(f'  {n}: aces={o:.3f} gamma8bit={round(o**(1/2.2)*255)}')
# 色相保持 ACES
def hue(rgb):
    R,G,B=rgb; Y=0.2126*R+0.7152*G+0.0722*B
    x=Y*100/100.0  # 相对白场? 实际用Y的nit/100
    k=aces(Y/0.01)/max(Y/0.01,1e-9)  # Y/0.01 = 相对白场(100nit=0.01线性)
    return tuple(np.clip(c*k,0,1) for c in rgb)
# 暗部/天空(线性光 [0,1]=10000nit, 100nit=0.01)
print('--- 色相保持 ACES(100nit白场) ---')
for n,rgb,t8 in [('暗部山脉',(0.0012,0.0005,0.0003),(18,2,3)),('天空',(0.048,0.050,0.054),(234,162,82))]:
    o=hue(rgb); g=tuple(round(float(x**(1/2.2)*255)) for x in o)
    print(f'  {n}: 8bit={g} 目标={t8}')
