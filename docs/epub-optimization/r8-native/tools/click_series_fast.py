import ctypes, sys, os, json, time
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try: ctypes.windll.user32.SetProcessDPIAware()
    except Exception: pass
user32=ctypes.windll.user32
def force_fg(hwnd):
    user32.ShowWindow(hwnd,9); user32.BringWindowToTop(hwnd)
    tid=user32.GetWindowThreadProcessId(hwnd,None); cur=ctypes.windll.kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(cur,tid,True); ok=user32.SetForegroundWindow(hwnd); user32.AttachThreadInput(cur,tid,False); return ok
def click(x,y):
    user32.SetCursorPos(int(x),int(y)); user32.mouse_event(0x0002,0,0,0,0); user32.mouse_event(0x0004,0,0,0,0)
def spine(path):
    try: return int(json.load(open(path,encoding='utf-8'))['position']['spineIndex'])
    except Exception: return None
def main():
    hwnd=int(sys.argv[1]); x=float(sys.argv[2]); y=float(sys.argv[3]); pos=sys.argv[4]; expect=int(sys.argv[5]); count=int(sys.argv[6]); progress=sys.argv[7]
    force_fg(hwnd); time.sleep(0.35); force_fg(hwnd); time.sleep(0.15)
    out={"startTime":time.time(),"hwnd":hwnd,"startSpine":spine(pos),"count":count,"clicks":[],"okCount":0}
    for i in range(1,count+1):
        want=expect+i; t0=time.time(); click(x,y); got=None
        while time.time()-t0 < 1.2:
            got=spine(pos)
            if got==want: break
            time.sleep(0.01)
        retried=False
        if got!=want:
            retried=True; force_fg(hwnd); time.sleep(0.08); click(x,y); t1=time.time()
            while time.time()-t1 < 1.5:
                got=spine(pos)
                if got==want: break
                time.sleep(0.01)
        ok=(got==want); out["okCount"]+=1 if ok else 0
        out["clicks"].append({"i":i,"spine":got,"ok":ok,"retried":retried,"t":time.time()})
        if i%20==0 or i==count:
            json.dump({"done":i,"spine":got,"okCount":out["okCount"],"last":out["clicks"][-20:]},open(progress,'w',encoding='utf-8'),ensure_ascii=False)
        time.sleep(0.02)
    out["endTime"]=time.time(); out["endSpine"]=spine(pos)
    json.dump(out,open(progress+'.result.json','w',encoding='utf-8'),ensure_ascii=False)
    json.dump({"done":count,"spine":out["endSpine"],"okCount":out["okCount"],"finished":True},open(progress,'w',encoding='utf-8'),ensure_ascii=False)
    print(json.dumps({"okCount":out["okCount"],"expected":count,"endSpine":out["endSpine"],"elapsed":out["endTime"]-out["startTime"]}))
if __name__=="__main__": main()