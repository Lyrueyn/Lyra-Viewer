// dpi_probe.cpp —— 查显示缩放：物理分辨率 vs 系统度量（非 DPI 感知进程会被虚拟化）+ 系统 DPI
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellscalingapi.h>
#include <cstdio>

int main() {
    int sx = GetSystemMetrics(SM_CXSCREEN), sy = GetSystemMetrics(SM_CYSCREEN);
    printf("GetSystemMetrics 虚拟化分辨率: %dx%d\n", sx, sy);

    DEVMODEW dm{}; dm.dmSize = sizeof(dm);
    if (EnumDisplaySettingsW(nullptr, ENUM_CURRENT_SETTINGS, &dm))
        printf("EnumDisplaySettings 物理分辨率: %lux%lu\n", (unsigned long)dm.dmPelsWidth, (unsigned long)dm.dmPelsHeight);

    HDC dc = GetDC(nullptr);
    int dpiX = GetDeviceCaps(dc, LOGPIXELSX);
    ReleaseDC(nullptr, dc);
    printf("GetDeviceCaps LOGPIXELSX: %d (96=100%%, 144=150%%, 192=200%%)\n", dpiX);

    // 每个显示器的有效缩放
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR h, HDC, LPRECT, LPARAM) -> BOOL {
        DEVICE_SCALE_FACTOR f = SCALE_100_PERCENT;
        if (SUCCEEDED(GetScaleFactorForMonitor(h, &f))) {
            MONITORINFOEXW mi{}; mi.cbSize = sizeof(mi);
            GetMonitorInfoW(h, &mi);
            printf("  显示器 %ls: 缩放 = %d%%\n", mi.szDevice, (int)f);
        }
        return TRUE;
    }, 0);
    printf("推断: 若物理 3840 而虚拟化度量更小，则处于缩放显示；弹幕/视频按逻辑尺寸建交换链会被合成器放大→发虚。\n");
    return 0;
}
