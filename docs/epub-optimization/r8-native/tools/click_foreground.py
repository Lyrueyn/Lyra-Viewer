import ctypes, sys, time
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
def click(hwnd,x,y):
    force_fg(hwnd); time.sleep(0.15)
    user32.SetCursorPos(int(x),int(y)); time.sleep(0.03)
    user32.mouse_event(0x0002,0,0,0,0); time.sleep(0.03); user32.mouse_event(0x0004,0,0,0,0)
    return True
if __name__=="__main__":
    print(click(int(sys.argv[1]),float(sys.argv[2]),float(sys.argv[3])))