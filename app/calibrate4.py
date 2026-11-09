import numpy as np
# 段落映射: 白场内线性(暗部黑), 超白场 reinhard 压缩。输入 R 是 pqToLin 输出 [0,1] 对应 0-10000nit (10000位), 白场npl=100nit=0.01
# 当前 shader: R*100 把 100nit(0.01) 归到 1。所以 R_npl = R*100。
# 分段: R_npl<=1 线性保留, >1 用 reinhard 只压高光
def tonemap_hi(R):
    xn = R*100.0     # 100nit 归1
    if xn<=1.0: return xn*0.9   # 白场内稍压保持(0.9防过白)
    return 1.0 + (xn-1.0)/(1.0+(xn-1.0))   # 超白场 reinhard

def tonemap_lin(R):
    xn=R*100.0
    return xn   # 纯线性(暗部黑, 但高光爆)

# 用日落真实锚点: 暗部 R线性 0.0012(G0.0005,B0.0003), 天空 0.048,0.050,0.054
def show(name, rgb):
    outs=[tonemap_hi(r) for r in rgb]
    g=[round(o**(1/2.2)*255) for o in outs]
    print(f'{name}: 线性{rgb} -> hi分段 8bit={g}')

show('暗部山脉',(0.0012,0.0005,0.0003))
show('天空(495nit)',(0.048,0.050,0.054))
# 目标参考: 暗[18,2,3] 天空[234,162,82]
print('目标: 暗[18,2,3] 天空[234,162,82]')
# 检查white场100nit
print('100nit白场: hi分段 8bit=%d' % round((tonemap_hi(0.01))**(1/2.2)*255))
