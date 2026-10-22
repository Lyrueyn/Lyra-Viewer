// dxgi_hdr_probe.cpp —— 查当前显示器/输出是否处于 Windows HDR（高级色彩）模式，以及亮度能力。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi1_6.h>
#include <cstdio>

static const char* csName(DXGI_COLOR_SPACE_TYPE t) {
    switch (t) {
        case DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709:      return "SDR (sRGB/gamma2.2)";
        case DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020:   return "HDR10 (PQ/BT.2020) ← Windows HDR 已开";
        case DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709:      return "linear scRGB";
        case DXGI_COLOR_SPACE_RGB_STUDIO_G2084_NONE_P2020: return "HDR10 (studio range)";
        default: return "其它/未知";
    }
}

int main() {
    IDXGIFactory1* fac = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac))) { printf("CreateDXGIFactory1 失败\n"); return 1; }
    for (UINT ai = 0;; ai++) {
        IDXGIAdapter1* ad = nullptr;
        if (fac->EnumAdapters1(ai, &ad) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 adesc{}; ad->GetDesc1(&adesc);
        wprintf(L"适配器 %u: %s\n", ai, adesc.Description);
        for (UINT oi = 0;; oi++) {
            IDXGIOutput* out = nullptr;
            if (ad->EnumOutputs(oi, &out) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC od{}; out->GetDesc(&od);
            wprintf(L"  输出 %u: %s\n", oi, od.DeviceName);
            IDXGIOutput6* o6 = nullptr;
            if (SUCCEEDED(out->QueryInterface(__uuidof(IDXGIOutput6), (void**)&o6)) && o6) {
                DXGI_OUTPUT_DESC1 d1{};
                if (SUCCEEDED(o6->GetDesc1(&d1))) {
                    printf("    当前色彩空间 : %s (raw=%d)\n", csName(d1.ColorSpace), (int)d1.ColorSpace);
                    printf("    位深         : %u bit/通道\n", d1.BitsPerColor);
                    printf("    亮度能力     : 最大 %.0f nit / 峰值 %.0f nit / 最小 %.4f nit（MaxCLL 参考 %.0f）\n",
                           d1.MaxLuminance, d1.MaxFullFrameLuminance, d1.MinLuminance, d1.MaxLuminance);
                    printf("    结论         : %s\n", d1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
                           ? "Windows HDR 已开启 → 可做 HDR 直通（10bit 交换链 + PQ 输出）"
                           : "当前是 SDR 模式 → 正确做法就是现在实现的“BT.2390 色调映射到 SDR”");
                }
                o6->Release();
            } else printf("    （拿不到 IDXGIOutput6，无法判断 HDR 状态）\n");
            out->Release();
        }
        ad->Release();
    }
    fac->Release();
    return 0;
}
