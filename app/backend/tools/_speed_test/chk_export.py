import ctypes, os, sys, hashlib
bin = r"F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64"
os.add_dll_directory(bin)
dll = os.path.join(bin, "dsh_engine.dll")
print("md5 =", hashlib.md5(open(dll,'rb').read()).hexdigest().upper()[:8])
h = ctypes.WinDLL(dll)
for n in ("DshEngine_GpuDead","DshEngine_Init","DshEngine_SetVolume","DshEngine_SetSpeed","DshEngine_GetPosMs"):
    try:
        f = getattr(h, n); print("  %-24s 导出存在 ✔" % n)
    except AttributeError:
        print("  %-24s 缺失 ✗" % n)
h2 = ctypes.WinDLL(dll)
g = h2.DshEngine_GpuDead; g.restype = ctypes.c_int
print("DshEngine_GpuDead() 调用（未初始化状态下）=", g())
