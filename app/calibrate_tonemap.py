# 设计并验证：分段色相保持 HDR->SDR tone-map，目标逼近 B站参考。
# 思路：算亮度，用亮度做单一映射系数 k（色相保持），再 gamma。
# 分段：暗部用较小增益保留黑度，高光温和压缩。
import numpy as np

def aces_tonemap(x):
    # ACES fit (Stephen Hill) 输入[0,∞] 输出[0,1], 暗部线性黑+高光柔和
    a,b,c,d,e,f = 0.24,0.43,0.54,0.46,0.01,1.0
    return np.clip((x*(a*x+b))/(x*(c*x+d)+e),0,1)

def seg_tonemap(x, npl):
    # 分段: 输入线性光 x(0-1=白场npl), 暗部线性, 高光reinhard
    xn = x*npl
    # 分段: x<=0.18 线性, 0.18~ 用 soft reihnard
    lo = xn*0.8
    hi = xn/(1+xn*0.6)
    t = np.clip((xn-0.18)/0.8,0,1)
    return lo*(1-t)+hi*t

# 用 B站参考目标验证: 模拟输出应给出暗部黑+天空橙
# ACES 对代表性输入(相对白场):
for name,x in [('暗部',0.02),('中调',0.3),('高光',2.0),('极亮',5.0)]:
    print(f'ACES {name}(x={x}): {aces_tonemap(x):.3f}')
print('---分段---')
for name,x in [('暗部',0.02),('中调',0.3),('高光',2.0),('极亮',5.0)]:
    print(f'分段 {name}(x={x}): {seg_tonemap(x,1.0):.3f}')

# 色相保持: 用亮度映射系数应用到RGB
def hue_preserve(RGB, npl=1.0):
    R,G,B=RGB
    Y=0.2126*R+0.7152*G+0.0722*B
    k=aces_tonemap(Y*npl)/max(Y*npl,1e-6)
    return tuple(np.clip(c*k,0,1) for c in (R,G,B))

# 模拟日落: 暗部山脉RGB线性(bt709近似) 红0.05 绿0.008 蓝0.012, 天空 红2.0 绿1.2 蓝0.4
print('---色相保持 ACES 映射---')
for name,rgb in [('暗部山脉',(0.05,0.008,0.012)),('天空',(2.0,1.2,0.4))]:
    out=hue_preserve(rgb)
    out8=tuple(round(c**(1/2.2)*255) for c in out)
    # 直接用out(已0-1)做gamma
    g=tuple(round(c**(1/2.2)*255) for c in out)
    print(f'{name}: 线性{rgb} -> out8={g} (目标: 暗~[18,2,3] 天空~[234,162,82])')
