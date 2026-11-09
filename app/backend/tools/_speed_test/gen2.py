import numpy as np, sys
rate=48000
def tone(f, sec, amp=0.5):
    t=np.arange(int(rate*sec))/rate
    return (amp*np.sin(2*np.pi*f*t)).astype('<f4')
np.stack([tone(1000.0,4.0)]*2,axis=1).reshape(-1).tofile(sys.argv[1]+r'\clean_1x.f32')   # 1.0x: 4s
np.stack([tone(1000.0,2.0)]*2,axis=1).reshape(-1).tofile(sys.argv[1]+r'\clean_2x.f32')   # 2.0x: 2s（时长减半，音高不变）
np.stack([tone(2000.0,2.0)]*2,axis=1).reshape(-1).tofile(sys.argv[1]+r'\badjmp_2x.f32')  # 假 2.0x：时长减半但变调
