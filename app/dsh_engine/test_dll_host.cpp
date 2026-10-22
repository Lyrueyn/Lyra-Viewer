// test_dll_host.cpp —— 无窗口宿主：直接加载 dsh_engine.dll，伪造 ISwapChainPanelNative 把交换链接过来，
// 播放/seek 后把 DLL 真实渲染出的帧回读成 BMP。用于验证"要发布的那份 DLL"端到端是否正常（含 HDR 路径）。
//
// 用法: test_dll_host.exe <视频> <seek毫秒> <输出.bmp> [等待毫秒]
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dxgi1_4.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <vector>
#include <cstdint>
#include <cmath>
#include <chrono>

// ---- 最小 ISwapChainPanelNative 实现（IID 与 WinUI 的 Dxinterop 一致）----
MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325") ISwapChainPanelNativeFake : public IUnknown {
public:
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* swapChain) = 0;
};
static const GUID IID_Fake_ = {0x63aad0b8,0x7c24,0x40ff,{0x85,0xa8,0x64,0x0d,0x94,0x4c,0xc3,0x25}};

class PanelFake : public ISwapChainPanelNativeFake {
public:
    IDXGISwapChain* swap = nullptr;
    LONG ref = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_Fake_) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&ref); }
    ULONG STDMETHODCALLTYPE Release() override { LONG r = InterlockedDecrement(&ref); if (!r) delete this; return (ULONG)r; }
    HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* s) override {
        if (swap) swap->Release();
        swap = s; if (swap) swap->AddRef();
        printf("[host] DLL 已把交换链接过来: %p\n", (void*)swap);
        return S_OK;
    }
};

// ---- DLL 导出 ----
typedef int  (WINAPI *PFN_Init)(void*, int, int);
typedef int  (WINAPI *PFN_OpenMedia)(const wchar_t*);
typedef void (WINAPI *PFN_Present)();
typedef void (WINAPI *PFN_SeekJump)(long long);
typedef void (WINAPI *PFN_SetPaused)(int);
typedef int  (WINAPI *PFN_IsHdr)();
typedef int  (WINAPI *PFN_HdrOutput)();
typedef int  (WINAPI *PFN_HdrAvail)();
typedef int  (WINAPI *PFN_SetHdrPass)(int);
typedef void (WINAPI *PFN_SetDm)(const wchar_t* const*, const int*, const unsigned*, const double*, int);
typedef void (WINAPI *PFN_SetDmMerge)(int);
typedef int  (WINAPI *PFN_DmLayout)(float*, float*, int*, int);   // 弹幕的光栅化在它里面（C# 每帧调）
typedef long long (WINAPI *PFN_GetClock)();
typedef void (WINAPI *PFN_SetScale)(float);
typedef float(WINAPI *PFN_ContentPeak)();
typedef long long (WINAPI *PFN_GetPosMs)();
typedef void (WINAPI *PFN_Shutdown)();

static void saveBMP24(const char* path, const uint8_t* bgra, int w, int h, int stride) {
    int rowSize = (w * 3 + 3) & ~3, dataSize = rowSize * h;
    std::vector<uint8_t> buf(54 + dataSize, 0);
    buf[0] = 'B'; buf[1] = 'M';
    int fileSize = 54 + dataSize, off = 54, ih = 40;
    memcpy(&buf[2], &fileSize, 4); memcpy(&buf[10], &off, 4); memcpy(&buf[14], &ih, 4);
    memcpy(&buf[18], &w, 4); memcpy(&buf[22], &h, 4);
    short planes = 1, bpp = 24;
    memcpy(&buf[26], &planes, 2); memcpy(&buf[28], &bpp, 2);
    memcpy(&buf[34], &dataSize, 4);
    for (int y = 0; y < h; y++) {
        const uint8_t* src = bgra + (size_t)(h - 1 - y) * stride;
        uint8_t* dst = buf.data() + 54 + (size_t)y * rowSize;
        for (int x = 0; x < w; x++) { dst[x*3+0] = src[x*4+0]; dst[x*3+1] = src[x*4+1]; dst[x*3+2] = src[x*4+2]; }
    }
    FILE* f = fopen(path, "wb");
    if (f) { fwrite(buf.data(), 1, buf.size(), f); fclose(f); printf("[host] 已写出 %s (%dx%d)\n", path, w, h); }
    else printf("[host] 写文件失败 %s\n", path);
}

// 10bit PQ 后缓冲 → BMP：解包 R10G10B10A2（低 10 位=R）并做 PQ→线性→sRGB 预览，
// 这样存出来的图是"把 HDR 内容当 SDR 看"的效果，用于确认直通路径确实在输出 PQ 码值。
static inline float pqToLin(float v) {
    float m1 = 2610.0f/16384.0f, m2 = 2523.0f/4096.0f*128.0f;
    float c1 = 3424.0f/4096.0f, c2 = 2413.0f/4096.0f*32.0f, c3 = 2392.0f/4096.0f*32.0f;
    float vp = powf(v > 0 ? v : 0, 1.0f/m2);
    float d = c2 - c3*vp; if (d < 1e-6f) d = 1e-6f;
    float q = (vp - c1) > 0 ? (vp - c1)/d : 0;
    return powf(q, 1.0f/m1);
}
static inline float srgbEnc(float c) { return c <= 0.0031308f ? c*12.92f : 1.055f*powf(c > 0 ? c : 0, 1.0f/2.4f) - 0.055f; }
static void saveBMP24From10(const char* path, const uint8_t* src10, int w, int h, int stride, float normNits) {
    int rowSize = (w * 3 + 3) & ~3, dataSize = rowSize * h;
    std::vector<uint8_t> buf(54 + dataSize, 0);
    buf[0] = 'B'; buf[1] = 'M';
    int fileSize = 54 + dataSize, off = 54, ih = 40;
    memcpy(&buf[2], &fileSize, 4); memcpy(&buf[10], &off, 4); memcpy(&buf[14], &ih, 4);
    memcpy(&buf[18], &w, 4); memcpy(&buf[22], &h, 4);
    short planes = 1, bpp = 24;
    memcpy(&buf[26], &planes, 2); memcpy(&buf[28], &bpp, 2);
    memcpy(&buf[34], &dataSize, 4);
    float norm = normNits / 10000.0f;    // PQ 归一化基准：10000=绝对亮度；203=按系统 SDR 白场（便于和 SDR 图对比）
    for (int y = 0; y < h; y++) {
        const uint8_t* row = src10 + (size_t)(h - 1 - y) * stride;
        uint8_t* dst = buf.data() + 54 + (size_t)y * rowSize;
        for (int x = 0; x < w; x++) {
            uint32_t w32; memcpy(&w32, row + (size_t)x*4, 4);
            float r = srgbEnc(pqToLin(((w32      ) & 0x3FF) / 1023.0f) / norm);
            float g = srgbEnc(pqToLin(((w32 >> 10) & 0x3FF) / 1023.0f) / norm);
            float b = srgbEnc(pqToLin(((w32 >> 20) & 0x3FF) / 1023.0f) / norm);
            dst[x*3+0] = (uint8_t)(b < 0 ? 0 : b > 1 ? 255 : b*255);
            dst[x*3+1] = (uint8_t)(g < 0 ? 0 : g > 1 ? 255 : g*255);
            dst[x*3+2] = (uint8_t)(r < 0 ? 0 : r > 1 ? 255 : r*255);
        }
    }
    FILE* f = fopen(path, "wb");
    if (f) { fwrite(buf.data(), 1, buf.size(), f); fclose(f); printf("[host] 已写出 %s (%dx%d, PQ→sRGB 预览, 归一化基准 %.0f nit)\n", path, w, h, normNits); }
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 4) { printf("usage: test_dll_host <video> <seekMs> <out.bmp> [waitMs]\n"); return 1; }
    const wchar_t* file = argv[1];                 // 宽字符入口：命令行里的中文路径不会被 GBK 弄坏
    long long seekMs = _wtoi64(argv[2]);
    char outPath[1024];
    wcstombs(outPath, argv[3], sizeof(outPath));
    int waitMs = argc > 4 ? _wtoi(argv[4]) : 1500;

    HMODULE dll = LoadLibraryW(L"dsh_engine.dll");
    if (!dll) { printf("[host] 加载 dsh_engine.dll 失败 (err=%lu)\n", GetLastError()); return 2; }
    auto pInit = (PFN_Init)GetProcAddress(dll, "DshEngine_Init");
    auto pOpen = (PFN_OpenMedia)GetProcAddress(dll, "DshEngine_OpenMedia");
    auto pPresent = (PFN_Present)GetProcAddress(dll, "DshEngine_Present");
    auto pSeek = (PFN_SeekJump)GetProcAddress(dll, "DshEngine_SeekJump");
    auto pPause = (PFN_SetPaused)GetProcAddress(dll, "DshEngine_SetPaused");
    auto pIsHdr = (PFN_IsHdr)GetProcAddress(dll, "DshEngine_IsHdr");
    auto pHdrOut = (PFN_HdrOutput)GetProcAddress(dll, "DshEngine_HdrOutput");
    auto pHdrAvail = (PFN_HdrAvail)GetProcAddress(dll, "DshEngine_HdrAvailable");
    auto pSetPass = (PFN_SetHdrPass)GetProcAddress(dll, "DshEngine_SetHdrPassthrough");
    auto pSetDm = (PFN_SetDm)GetProcAddress(dll, "DshEngine_SetDanmaku");
    auto pSetDmMerge = (PFN_SetDmMerge)GetProcAddress(dll, "DshEngine_SetDanmakuMerge");
    auto pDmLayout = (PFN_DmLayout)GetProcAddress(dll, "DshEngine_GetDanmakuLayout");
    auto pClock = (PFN_GetClock)GetProcAddress(dll, "DshEngine_GetClockPosMs");
    auto pSetScale = (PFN_SetScale)GetProcAddress(dll, "DshEngine_SetUiScale");
    auto pPeak = (PFN_ContentPeak)GetProcAddress(dll, "DshEngine_ContentPeak");
    auto pPos = (PFN_GetPosMs)GetProcAddress(dll, "DshEngine_GetPosMs");
    auto pShutdown = (PFN_Shutdown)GetProcAddress(dll, "DshEngine_Shutdown");
    if (!pInit || !pOpen || !pPresent || !pSeek || !pPause || !pShutdown) { printf("[host] 缺少必要导出\n"); return 2; }
    printf("[host] 导出齐全 (IsHdr=%p ContentPeak=%p)\n", (void*)pIsHdr, (void*)pPeak);

    const int W = 1280, H = 720;   // 与真实视频窗接近的尺寸
    float uiScale = 1.0f;
    if (const char* e = getenv("HOST_UI_SCALE")) { float v = (float)atof(e); if (v >= 0.5f && v <= 4.0f) uiScale = v; }
    PanelFake* panel = new PanelFake();
    if (pSetScale) pSetScale(uiScale);                                  // 模拟 C# 传 RasterizationScale
    int rc = pInit(panel, (int)(W * uiScale), (int)(H * uiScale));      // 交换链按物理像素建
    printf("[host] UI 缩放=%.2f → 渲染目标 %dx%d\n", uiScale, (int)(W * uiScale), (int)(H * uiScale));
    printf("[host] DshEngine_Init rc=%d\n", rc);
    int orc = pOpen(file);
    printf("[host] DshEngine_OpenMedia rc=%d\n", orc);
    if (pIsHdr) printf("[host] DshEngine_IsHdr=%d\n", pIsHdr());

    // 播放一会儿（等解码线程出帧），再 seek 到目标位置
    for (int i = 0; i < 60; i++) { pPresent(); Sleep(16); }
    pSeek(seekMs);
    printf("[host] 已 seek 到 %lld ms\n", seekMs);
    for (int i = 0; i < waitMs / 16; i++) { pPresent(); Sleep(16); }
    // 可选：运行中拨 HDR 开关（argv[5]=1 → 请求直通），验证"不用重启就能切"
    bool toggleHdr = (argc > 5 && _wtoi(argv[5]) != 0);
    int toggleRounds = (argc > 5) ? _wtoi(argv[5]) : 0;   // 1=切到直通；2=直通再切回色调映射
    if (toggleHdr && pSetPass) {
        printf("[host] 拨开关 DshEngine_SetHdrPassthrough(1) → 返回 %d\n", pSetPass(1));
        for (int i = 0; i < 40; i++) { pPresent(); Sleep(16); }   // 跑几帧让交换链重建生效
        if (pHdrOut) printf("[host] 切到直通后 HdrOutput=%d\n", pHdrOut());
        if (toggleRounds >= 2) {
            printf("[host] 再拨回 DshEngine_SetHdrPassthrough(0) → 返回 %d\n", pSetPass(0));
            for (int i = 0; i < 40; i++) { pPresent(); Sleep(16); }
            if (pHdrOut) printf("[host] 切回映射后 HdrOutput=%d\n", pHdrOut());
        }
    }
    // 可选：喂几条**彩色固定弹幕**（argv[6]=dm）—— 顶部固定不移动，便于抓帧比对 HDR 切换前后的弹幕颜色
    bool feedDm = (argc > 6 && wcscmp(argv[6], L"dm") == 0);
    if (feedDm && pSetDm) {
        long long nowMs = pClock ? pClock() : 0;
        double t0 = nowMs / 1000.0 + 0.3;   // 用 DLL 的真实时钟定出生时间（Sleep 循环实际耗时远大于名义值）
        int dmCount = 5;
        if (const char* e = getenv("HOST_DM_COUNT")) { int v = atoi(e); if (v > 0 && v <= 600) dmCount = v; }
        bool dense = (dmCount > 5);
        printf("[host] 当前播放时钟=%lld ms → 弹幕出生时间 %.2fs（喂 %d 条）\n", nowMs, t0, dmCount);
        std::vector<const wchar_t*> texts; std::vector<int> modes; std::vector<unsigned> cols; std::vector<double> times;
        static const wchar_t* pool[5] = { L"WHITE", L"YELLOW", L"RED", L"BLUE", L"GREEN" };
        static const unsigned colpool[5] = { 0xFFFFFF, 0xFFD700, 0xFF3020, 0x3CA0FF, 0x40D060 };
        for (int i = 0; i < dmCount; i++) {
            texts.push_back(pool[i % 5]);
            modes.push_back(dense ? 1 : 5);                 // 压测用滚动(1)，比对颜色用顶部固定(5)
            cols.push_back(colpool[i % 5]);
            times.push_back(t0 + i * 0.02);
        }
        if (pSetDmMerge) pSetDmMerge(3000);
        pSetDm(texts.data(), modes.data(), cols.data(), times.data(), dmCount);
        printf("[host] 已喂 %d 条弹幕\n", dmCount);
        double accum = 0; const int frames = 120;
        for (int i = 0; i < frames; i++) {           // 每帧必须调 GetDanmakuLayout：弹幕的光栅化在里面做
            auto t1 = std::chrono::steady_clock::now();
            if (pDmLayout) pDmLayout(nullptr, nullptr, nullptr, 0);
            auto t2 = std::chrono::steady_clock::now();
            accum += std::chrono::duration<double, std::milli>(t2 - t1).count();
            pPresent(); Sleep(16);
        }
        if (pDmLayout) printf("[host] 在飞弹幕=%d；%d 帧平均光栅化 %.2f ms/帧\n",
                              pDmLayout(nullptr, nullptr, nullptr, 0), frames, accum / frames);
        // 诊断：把在飞弹幕的坐标取出来，看行距是否符合"字高+行距"
        if (pDmLayout) {
            float xs[128]; float ys[128]; int cols[128];
            int n = pDmLayout(xs, ys, cols, 128);
            if (n > 128) n = 128;
            printf("[host] 前 %d 条坐标: ", n);
            for (int i = 0; i < n && i < 24; i++) printf("(%.0f,%.0f) ", xs[i], ys[i]);
            printf("\n");
        }
    }
    pPause(1);                     // 暂停：之后每 tick 画同一帧，回读稳定
    for (int i = 0; i < 12; i++) { pPresent(); Sleep(16); }
    if (pPos) printf("[host] 当前播放位置=%lld ms\n", pPos());
    if (pIsHdr) printf("[host] IsHdr=%d  HdrOutput=%d  ContentPeak=%.1f nit\n",
                       pIsHdr(), pHdrOut ? pHdrOut() : -1, pPeak ? pPeak() : 0.0f);

    // ---- 回读 DLL 渲染的帧：两个后缓冲都读一遍，取"非黑且最亮"的那张 ----
    IDXGISwapChain* swap = panel->swap;
    if (!swap) { printf("[host] DLL 没有交出交换链\n"); return 3; }
    ID3D11Texture2D* bb0 = nullptr;
    if (FAILED(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb0)) || !bb0) { printf("[host] 取后缓冲失败\n"); return 3; }
    D3D11_TEXTURE2D_DESC bd{}; bb0->GetDesc(&bd);
    DXGI_SWAP_CHAIN_DESC1 scd{}; UINT bufCount = 2;
    { IDXGISwapChain1* s1 = nullptr; if (SUCCEEDED(swap->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&s1)) && s1) { s1->GetDesc1(&scd); if (scd.BufferCount) bufCount = scd.BufferCount; s1->Release(); } }
    printf("[host] 后缓冲 %ux%u fmt=%d count=%u\n", bd.Width, bd.Height, (int)bd.Format, bufCount);

    ID3D11Device* dev = nullptr; bb0->GetDevice(&dev);
    ID3D11DeviceContext* ctx = nullptr; dev->GetImmediateContext(&ctx);
    D3D11_TEXTURE2D_DESC sd{}; sd.Width = bd.Width; sd.Height = bd.Height; sd.MipLevels = 1; sd.ArraySize = 1;
    sd.Format = bd.Format; sd.SampleDesc.Count = 1; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* stage = nullptr;
    if (FAILED(dev->CreateTexture2D(&sd, nullptr, &stage))) { printf("[host] staging 创建失败\n"); return 3; }

    double bestMean = -1; std::vector<uint8_t> bestPix; int bestIdx = -1;
    for (UINT i = 0; i < bufCount && i < 4; i++) {
        ID3D11Texture2D* b = nullptr;
        if (FAILED(swap->GetBuffer(i, __uuidof(ID3D11Texture2D), (void**)&b)) || !b) continue;
        ctx->CopyResource(stage, b);
        D3D11_MAPPED_SUBRESOURCE m{};
        if (SUCCEEDED(ctx->Map(stage, 0, D3D11_MAP_READ, 0, &m))) {
            double sum = 0; size_t n = 0;
            for (UINT y = 0; y < bd.Height; y += 4) {
                const uint8_t* row = (const uint8_t*)m.pData + (size_t)y * m.RowPitch;
                for (UINT x = 0; x < bd.Width; x += 4) {
                    sum += row[x*4+0] * 0.114 + row[x*4+1] * 0.587 + row[x*4+2] * 0.299; n++;
                }
            }
            double mean = n ? sum / n : 0;
            printf("[host] 缓冲#%u 平均亮度=%.2f\n", i, mean);
            if (mean > bestMean) {
                bestMean = mean; bestIdx = (int)i;
                bestPix.assign((size_t)m.RowPitch * bd.Height, 0);
                memcpy(bestPix.data(), m.pData, (size_t)m.RowPitch * bd.Height);
            }
            ctx->Unmap(stage, 0);
        }
        b->Release();
    }
    if (bestIdx < 0) { printf("[host] 没读到任何缓冲\n"); return 3; }
    printf("[host] 采用缓冲#%d（平均亮度 %.2f）\n", bestIdx, bestMean);
    if (bd.Format == DXGI_FORMAT_R10G10B10A2_UNORM) {
        float norm = 10000.0f;                                   // 默认按绝对亮度预览
        if (const char* e = getenv("HOST_PQ_NORM")) { float v = (float)atof(e); if (v > 1.0f) norm = v; }   // 可比对时按 SDR 白场
        saveBMP24From10(outPath, bestPix.data(), (int)bd.Width, (int)bd.Height, (int)(bestPix.size() / bd.Height), norm);
    } else
        saveBMP24(outPath, bestPix.data(), (int)bd.Width, (int)bd.Height, (int)(bestPix.size() / bd.Height));
    pShutdown();
    printf("[host] 完成\n");
    return 0;
}
