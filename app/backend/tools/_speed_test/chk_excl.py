import ctypes, os, hashlib
bin = r"F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"
os.add_dll_directory(bin)
dll = os.path.join(bin, "dsh_engine.dll")
print("md5 =", hashlib.md5(open(dll,'rb').read()).hexdigest().upper()[:8])
h = ctypes.WinDLL(dll)
for n in ("DshEngine_SetExclusive","DshEngine_GetExclusive","DshEngine_GetExclusiveActive","DshEngine_GpuDead"):
    try:
        getattr(h, n); print("  %-30s 导出存在" % n)
    except AttributeError:
        print("  %-30s 缺失 ✗" % n)
h2 = ctypes.WinDLL(dll)
try:
    print("未初始化调用 GetExclusive() =", h2.DshEngine_GetExclusive())
    print("未初始化调用 GetExclusiveActive() =", h2.DshEngine_GetExclusiveActive())
except Exception as e:
    print("未初始化调用异常:", e)
