// rtx_vsr_bridge.dll —— 给 MinGW 编的 dsh_engine.dll 动态调用：
// 把 NVIDIA RTX Video SDK (DX11) 包成 C 接口（不用 MSVC 链接进引擎，避免 ABI 冲突）。
// 两种用法：
//   1) 引擎 D3D11 路径：Rv_Init(engineDev,outW,outH) 后 Rv_Evaluate(videoTex,...)（GPU 内直接画）
//   2) 引擎 Vulkan 路径：Rv_Init(engineDev,outW,outH) 后 Rv_ProcessCpu(CPU帧→CPU帧)（D3D11 上跑完回读，再交给 Vulkan 上传）
// 编译：MSVC + RTX_Video_API.lib + nvsdk_ngx_s.lib（运行时 nvngx_vsr.dll / nvngx_truehdr.dll 放 exe 目录）
#include <windows.h>
#include <d3d11.h>
#include "rtx_video_api.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

static ID3D11Device* g_dev = nullptr;
static ID3D11DeviceContext* g_ctx = nullptr;
static ID3D11Texture2D* g_outTex = nullptr;
static ID3D11ShaderResourceView* g_outSrv = nullptr;
static ID3D11Texture2D* g_inTex = nullptr;
static int g_inW = 0, g_inH = 0;
static ID3D11Texture2D* g_stageTex = nullptr;
static int g_stageW = 0, g_stageH = 0;
static int g_outW = 0, g_outH = 0;
static int g_ready = 0;

static void vsr_internal_shutdown() {
    if (g_ready) {
        rtx_video_api_dx11_shutdown();
        g_ready = 0;
    }
    if (g_outSrv) { g_outSrv->Release(); g_outSrv = nullptr; }
    if (g_outTex) { g_outTex->Release(); g_outTex = nullptr; }
    if (g_inTex) { g_inTex->Release(); g_inTex = nullptr; g_inW = g_inH = 0; }
    if (g_stageTex) { g_stageTex->Release(); g_stageTex = nullptr; g_stageW = g_stageH = 0; }
    if (g_ctx) { g_ctx->Release(); g_ctx = nullptr; }
    if (g_dev) { g_dev->Release(); g_dev = nullptr; }
    g_outW = g_outH = 0;
}

extern "C" {

__declspec(dllexport) int Rv_Probe() {
    ID3D11Device* d = nullptr;
    ID3D11DeviceContext* c = nullptr;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                                   nullptr, 0, D3D11_SDK_VERSION, &d, &fl, &c);
    if (FAILED(hr) || !d) return 0;
    int ok = rtx_video_api_dx11_create(d, FALSE, TRUE) ? 1 : 0;
    if (ok) rtx_video_api_dx11_shutdown();
    c->Release();
    d->Release();
    return ok;
}

// pDev = 引擎的 ID3D11Device*；outW/outH = VSR 输出纹理尺寸（尺寸变了会重建）
__declspec(dllexport) int Rv_Init(void* pDev, int outW, int outH) {
    if (!pDev || outW <= 0 || outH <= 0) return 0;
    if (g_ready) {
        if (g_outW == outW && g_outH == outH) return 1;
        vsr_internal_shutdown();
    }
    g_dev = (ID3D11Device*)pDev;
    g_dev->AddRef();
    g_dev->GetImmediateContext(&g_ctx);
    if (!g_ctx) { vsr_internal_shutdown(); return 0; }
    if (!rtx_video_api_dx11_create(g_dev, FALSE, TRUE)) { vsr_internal_shutdown(); return 0; }
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = (UINT)outW; td.Height = (UINT)outH; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    HRESULT hr = g_dev->CreateTexture2D(&td, nullptr, &g_outTex);
    if (FAILED(hr) || !g_outTex) { vsr_internal_shutdown(); return 0; }
    hr = g_dev->CreateShaderResourceView(g_outTex, nullptr, &g_outSrv);
    if (FAILED(hr) || !g_outSrv) { vsr_internal_shutdown(); return 0; }
    g_outW = outW; g_outH = outH; g_ready = 1;
    return 1;
}

__declspec(dllexport) void* Rv_OutputSRV() { return (void*)g_outSrv; }
__declspec(dllexport) int Rv_OutW() { return g_outW; }
__declspec(dllexport) int Rv_OutH() { return g_outH; }

// 引擎 D3D11 路径：pIn = 引擎 videoTex（BGRA8，inW x inH），输出写进 Rv_Init 创建的 out 纹理（GPU 内）
__declspec(dllexport) int Rv_Evaluate(void* pIn, int inW, int inH, int outW, int outH, int quality) {
    if (!g_ready || !pIn) return 0;
    if (inW <= 0 || inH <= 0 || outW <= 0 || outH <= 0) return 0;
    API_VSR_Setting setting = {};
    setting.QualityLevel = (UINT)(quality < 1 ? 1 : (quality > 4 ? 4 : quality));
    API_RECT inRect = {0, 0, (LONG)inW, (LONG)inH};
    API_RECT outRect = {0, 0, (LONG)outW, (LONG)outH};
    return rtx_video_api_dx11_evaluate((ID3D11Texture2D*)pIn, g_outTex, inRect, outRect, &setting, nullptr) ? 1 : 0;
}

// 引擎 Vulkan 路径：CPU BGRA 输入 → VSR → CPU BGRA 输出（D3D11 内跑完回读；引擎再喂给 Vulkan 上传）
__declspec(dllexport) int Rv_ProcessCpu(const void* inBgra, int inW, int inH,
                                        void* outBgra, int outW, int outH, int quality) {
    if (!g_ready || !inBgra || !outBgra) return 0;
    if (inW <= 0 || inH <= 0 || outW <= 0 || outH <= 0) return 0;
    if (outW != g_outW || outH != g_outH) return 0;

    // 输入纹理（尺寸变了重建）
    if (!g_inTex || g_inW != inW || g_inH != inH) {
        if (g_inTex) { g_inTex->Release(); g_inTex = nullptr; }
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = (UINT)inW; td.Height = (UINT)inH; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_dev->CreateTexture2D(&td, nullptr, &g_inTex)) || !g_inTex) return 0;
        g_inW = inW; g_inH = inH;
    }
    // staging（回读用）
    if (!g_stageTex || g_stageW != outW || g_stageH != outH) {
        if (g_stageTex) { g_stageTex->Release(); g_stageTex = nullptr; }
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = (UINT)outW; td.Height = (UINT)outH; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(g_dev->CreateTexture2D(&td, nullptr, &g_stageTex)) || !g_stageTex) return 0;
        g_stageW = outW; g_stageH = outH;
    }

    g_ctx->UpdateSubresource(g_inTex, 0, nullptr, inBgra, (UINT)(inW * 4), 0);
    API_VSR_Setting setting = {};
    setting.QualityLevel = (UINT)(quality < 1 ? 1 : (quality > 4 ? 4 : quality));
    API_RECT inRect = {0, 0, (LONG)inW, (LONG)inH};
    API_RECT outRect = {0, 0, (LONG)outW, (LONG)outH};
    if (!rtx_video_api_dx11_evaluate(g_inTex, g_outTex, inRect, outRect, &setting, nullptr)) return 0;

    g_ctx->CopyResource(g_stageTex, g_outTex);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(g_ctx->Map(g_stageTex, 0, D3D11_MAP_READ, 0, &m))) return 0;
    unsigned char* dst = (unsigned char*)outBgra;
    for (int y = 0; y < outH; ++y)
        memcpy(dst + (size_t)y * outW * 4, (unsigned char*)m.pData + (size_t)y * m.RowPitch, (size_t)outW * 4);
    g_ctx->Unmap(g_stageTex, 0);
    return 1;
}

__declspec(dllexport) void Rv_Shutdown() { vsr_internal_shutdown(); }

}  // extern "C"
