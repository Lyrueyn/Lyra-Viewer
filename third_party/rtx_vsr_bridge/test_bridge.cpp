// 验证 rtx_vsr_bridge.dll：宿主自己建 D3D11 设备/输入纹理，调桥接接口跑 VSR 并读回输出
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <chrono>
#include <iostream>

typedef int (*PFN_Probe)();
typedef int (*PFN_Init)(void*, int, int);
typedef int (*PFN_Eval)(void*, int, int, int, int, int);
typedef void* (*PFN_SRV)();
typedef void (*PFN_Shutdown)();

static bool read_raw(const char* p, void* d, size_t n) {
    FILE* f = fopen(p, "rb"); if (!f) return false;
    size_t r = fread(d, 1, n, f); fclose(f); return r == n;
}
static bool write_raw(const char* p, const void* d, size_t n) {
    FILE* f = fopen(p, "wb"); if (!f) return false;
    fwrite(d, 1, n, f); fclose(f); return true;
}

int main(int argc, char** argv) {
    const char* dllPath = argv[1];
    const char* inPath = argv[2];
    const char* outPath = argv[3];
    int inW = atoi(argv[4]), inH = atoi(argv[5]);
    int outW = atoi(argv[6]), outH = atoi(argv[7]);
    int quality = (argc > 8) ? atoi(argv[8]) : 2;
    int loop = (argc > 9) ? atoi(argv[9]) : 100;
    const int BPP = 4;

    HMODULE h = LoadLibraryA(dllPath);
    if (!h) { std::cerr << "LoadLibrary 失败 " << GetLastError() << "\n"; return 2; }
    auto pProbe = (PFN_Probe)GetProcAddress(h, "Rv_Probe");
    auto pInit = (PFN_Init)GetProcAddress(h, "Rv_Init");
    auto pEval = (PFN_Eval)GetProcAddress(h, "Rv_Evaluate");
    auto pSrv = (PFN_SRV)GetProcAddress(h, "Rv_OutputSRV");
    auto pShut = (PFN_Shutdown)GetProcAddress(h, "Rv_Shutdown");
    if (!pProbe || !pInit || !pEval || !pSrv || !pShut) { std::cerr << "导出不全\n"; return 2; }

    std::cout << "Rv_Probe = " << pProbe() << "\n";

    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr; D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                                   D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr) || !dev) { std::cerr << "D3D11CreateDevice 失败\n"; return 3; }

    std::vector<unsigned char> inBuf((size_t)inW * inH * BPP);
    if (!read_raw(inPath, inBuf.data(), inBuf.size())) { std::cerr << "读不到输入\n"; return 4; }

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = (UINT)inW; td.Height = (UINT)inH; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA sd = {}; sd.pSysMem = inBuf.data(); sd.SysMemPitch = (UINT)(inW * BPP);
    ID3D11Texture2D* inTex = nullptr;
    if (FAILED(dev->CreateTexture2D(&td, &sd, &inTex))) { std::cerr << "输入纹理失败\n"; return 5; }

    int initOk = pInit(dev, outW, outH);
    std::cout << "Rv_Init = " << initOk << "\n";
    if (!initOk) return 6;

    auto t0 = std::chrono::high_resolution_clock::now();
    int okCount = 0;
    for (int i = 0; i < loop; ++i) {
        ctx->UpdateSubresource(inTex, 0, nullptr, inBuf.data(), (UINT)(inW * BPP), 0);
        okCount += pEval(inTex, inW, inH, outW, outH, quality) ? 1 : 0;
    }
    // 读回：从桥接返回的 SRV 拿到它的 resource → 拷到 staging
    ID3D11ShaderResourceView* srv = (ID3D11ShaderResourceView*)pSrv();
    if (!srv) { std::cerr << "输出 SRV 为空\n"; return 7; }
    ID3D11Resource* outRes = nullptr;
    srv->GetResource(&outRes);
    ID3D11Texture2D* outTex = nullptr;
    outRes->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&outTex);
    D3D11_TEXTURE2D_DESC od = {}; outTex->GetDesc(&od);
    D3D11_TEXTURE2D_DESC st = od;
    st.Usage = D3D11_USAGE_STAGING; st.BindFlags = 0; st.CPUAccessFlags = D3D11_CPU_ACCESS_READ; st.MiscFlags = 0;
    ID3D11Texture2D* stage = nullptr;
    dev->CreateTexture2D(&st, nullptr, &stage);
    ctx->CopyResource(stage, outTex);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(ctx->Map(stage, 0, D3D11_MAP_READ, 0, &m))) { std::cerr << "Map 失败\n"; return 8; }
    auto t1 = std::chrono::high_resolution_clock::now();
    double ms = std::chrono::duration<double, std::milli>(t1 - t0).count() / loop;

    std::vector<unsigned char> outBuf((size_t)outW * outH * BPP);
    for (int y = 0; y < outH; ++y)
        memcpy(outBuf.data() + (size_t)y * outW * BPP,
               (unsigned char*)m.pData + (size_t)y * m.RowPitch, (size_t)outW * BPP);
    ctx->Unmap(stage, 0);
    write_raw(outPath, outBuf.data(), outBuf.size());

    std::cout << "eval ok " << okCount << "/" << loop << "  含读回均 " << ms << " ms/frame\n";
    std::cout << "输出纹理 " << od.Width << "x" << od.Height << " fmt=" << od.Format << "\n";

    stage->Release(); outTex->Release(); outRes->Release();
    inTex->Release(); ctx->Release(); dev->Release();
    pShut();
    FreeLibrary(h);
    return 0;
}
