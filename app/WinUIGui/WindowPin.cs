// ★ 窗口置顶（Pin / 总在最前）：用 Win32 SetWindowPos 设置 WS_EX_TOPMOST
//   主窗（视频窗口）和节点图窗口都用这个。
using System;
using System.Runtime.InteropServices;

namespace WinUIGui;

public static class WindowPin
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008L;

    /// <summary>设置窗口是否置顶；返回是否成功。</summary>
    public static bool Set(Microsoft.UI.Xaml.Window window, bool on)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return false;
            return SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { return false; }
    }

    /// <summary>查询窗口当前是否真的置顶（WS_EX_TOPMOST）。</summary>
    public static bool IsTopMost(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return false;
            long ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            return (ex & WS_EX_TOPMOST) != 0;
        }
        catch { return false; }
    }
}
