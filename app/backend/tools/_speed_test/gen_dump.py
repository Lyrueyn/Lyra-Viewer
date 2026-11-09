import numpy as np, sys
rate=48000
def tone(f, sec=2.0, amp=0.5):
    t=np.arange(int(rate*sec))/rate
    return (amp*np.sin(2*np.pi*f*t)).astype('<f4')
a=tone(1000.0)
a[int(0.8*rate):int(0.92*rate)]=0.0
np.stack([a,a],axis=1).reshape(-1).tofile(sys.argv[1]+r'\syn_1k.f32')
b=tone(2000.0)
np.stack([b,b],axis=1).reshape(-1).tofile(sys.argv[1]+r'\syn_2k.f32')
c=tone(1000.0,1.0)
np.concatenate([np.stack([c,c],axis=1).reshape(-1), np.zeros(3,dtype='<f4')]).tofile(sys.argv[1]+r'\syn_1k_half.f32')
print('generated')
