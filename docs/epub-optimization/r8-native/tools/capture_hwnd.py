import ctypes, sys, os, json
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try: ctypes.windll.user32.SetProcessDPIAware()
    except Exception: pass
from ctypes import wintypes
from PIL import Image
user32=ctypes.windll.user32; gdi32=ctypes.windll.gdi32
class RECT(ctypes.Structure):
    _fields_=[("left",wintypes.LONG),("top",wintypes.LONG),("right",wintypes.LONG),("bottom",wintypes.LONG)]
def capture(hwnd,path):
    if not user32.IsWindow(hwnd): raise SystemExit("bad hwnd")
    r=RECT(); user32.GetWindowRect(hwnd,ctypes.byref(r))
    w=r.right-r.left; h=r.bottom-r.top
    if w<=0 or h<=0: raise SystemExit("bad rect")
    hdc=user32.GetWindowDC(hwnd); mem=gdi32.CreateCompatibleDC(hdc); bmp=gdi32.CreateCompatibleBitmap(hdc,w,h); old=gdi32.SelectObject(mem,bmp)
    ok=user32.PrintWindow(hwnd,mem,2)
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_=[("biSize",wintypes.DWORD),("biWidth",wintypes.LONG),("biHeight",wintypes.LONG),("biPlanes",wintypes.WORD),("biBitCount",wintypes.WORD),("biCompression",wintypes.DWORD),("biSizeImage",wintypes.DWORD),("biXPelsPerMeter",wintypes.LONG),("biYPelsPerMeter",wintypes.LONG),("biClrUsed",wintypes.DWORD),("biClrImportant",wintypes.DWORD)]
    bi=BITMAPINFOHEADER(); bi.biSize=ctypes.sizeof(bi); bi.biWidth=w; bi.biHeight=-h; bi.biPlanes=1; bi.biBitCount=32; bi.biCompression=0
    buf=ctypes.create_string_buffer(w*h*4)
    got=gdi32.GetDIBits(mem,bmp,0,h,buf,ctypes.byref(bi),0)
    gdi32.SelectObject(mem,old); gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(hwnd,hdc)
    im=Image.frombuffer('RGB',(w,h),buf,'raw','BGRX',0,1); im.save(path)
    extrema=im.convert('L').getextrema()
    return {"hwnd":hwnd,"width":w,"height":h,"printwindow":bool(ok),"getdibits":got,"grayscale_min":extrema[0],"grayscale_max":extrema[1],"file":path,"bytes":os.path.getsize(path)}
if __name__=="__main__":
    print(json.dumps(capture(int(sys.argv[1]),sys.argv[2]),ensure_ascii=False))