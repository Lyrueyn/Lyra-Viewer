import ctypes, sys, os, json, time
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try: ctypes.windll.user32.SetProcessDPIAware()
    except Exception: pass
user32=ctypes.windll.user32
def force_fg(hwnd):
    user32.ShowWindow(hwnd,9); user32.BringWindowToTop(hwnd)
    tid=user32.GetWindowThreadProcessId(hwnd,None); cur=ctypes.windll.kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(cur,tid,True); ok=user32.SetForegroundWindow(hwnd); user32.AttachThreadInput(cur,tid,False)
    return ok
def click(x,y):
    user32.SetCursorPos(int(x),int(y)); time.sleep(0.02)
    user32.mouse_event(0x0002,0,0,0,0); time.sleep(0.02); user32.mouse_event(0x0004,0,0,0,0)
def read_spine(path):
    try:
        with open(path,'r',encoding='utf-8') as f: j=json.load(f)
        return int(j['position']['spineIndex'])
    except Exception: return None
def main():
    hwnd=int(sys.argv[1]); x=float(sys.argv[2]); y=float(sys.argv[3]); pos=sys.argv[4]; expect=int(sys.argv[5]); count=int(sys.argv[6]); progress=sys.argv[7]
    force_fg(hwnd); time.sleep(0.4)
    # 点击标题栏空白处确保阅读器拿到激活，不改变正文
    out={"startTime":time.time(),"hwnd":hwnd,"click":[x,y],"startSpine":read_spine(pos),"count":count,"clicks":[],"errors":[]}
    force_fg(hwnd); time.sleep(0.25)
    click(x,y)
    # 第一次若因激活被吞掉，最多补两次
    for retry in range(3):
        time.sleep(0.4)
        cur=read_spine(pos)
        if cur==expect+1: break
        force_fg(hwnd); time.sleep(0.15); click(x,y)
    cur=read_spine(pos)
    out["clicks"].append({"i":1,"spine":cur,"ok":cur==expect+1,"t":time.time()})
    done=1
    with open(progress,'w',encoding='utf-8') as f: json.dump({"done":done,"spine":cur,"snapshots":out["clicks"]},f,ensure_ascii=False)
    okcount=1 if cur==expect+1 else 0
    for i in range(2,count+1):
        want=expect+i
        force_fg(hwnd); time.sleep(0.05); click(x,y)
        got=None; t0=time.time()
        while time.time()-t0 < 2.0:
            got=read_spine(pos)
            if got==want: break
            time.sleep(0.02)
        if got!=want:
            force_fg(hwnd); time.sleep(0.12); click(x,y)
            t1=time.time()
            while time.time()-t1 < 2.0:
                got=read_spine(pos)
                if got==want: break
                time.sleep(0.02)
        ok=(got==want); okcount+=1 if ok else 0
        rec={"i":i,"spine":got,"ok":ok,"t":time.time()}
        out["clicks"].append(rec)
        if i%20==0 or i==count:
            with open(progress,'w',encoding='utf-8') as f: json.dump({"done":i,"spine":got,"okCount":okcount,"snapshots":out["clicks"][-20:]},f,ensure_ascii=False)
        time.sleep(0.08)
    out["endTime"]=time.time(); out["okCount"]=okcount; out["endSpine"]=read_spine(pos)
    with open(progress,'w',encoding='utf-8') as f: json.dump({"done":count,"spine":out["endSpine"],"okCount":okcount,"finished":True},f,ensure_ascii=False)
    with open(progress+'.result.json','w',encoding='utf-8') as f: json.dump(out,f,ensure_ascii=False)
    print(json.dumps({"okCount":okcount,"expected":count,"endSpine":out["endSpine"],"elapsed":out["endTime"]-out["startTime"]}))
if __name__=="__main__": main()