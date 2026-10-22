// wasapi_sink.cpp：WASAPI 流式输出实现（显式 GUID，兼容 MinGW）。
// 支持共享模式（SHARED，走系统混音器）与独占模式（EXCLUSIVE，绕过混音器，按协商格式输出）。
// 共享模式几乎总能成功（混音器自动处理格式）；独占模式按候选格式列表逐个协商，选第一个设备支持的。
#define WIN32_LEAN_AND_MEAN
#define INITGUID
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <mmreg.h>
#include <propidl.h>
#include <functiondiscoverykeys_devpkey.h>        // WAVEFORMATEXTENSIBLE / KSDATAFORMAT_SUBTYPE_*
#include <sys/types.h>
#include <algorithm>
#include <cstring>
#include <cmath>   // tanhf（软限幅实时增益控制）
#include "wasapi_sink.h"

// MinGW 下 KSDATAFORMAT_SUBTYPE_* 依赖 ksguid 库（未链接），这里手动给出等价的 GUID 常量。
// IEEE_FLOAT = 00000003-0000-0010-8000-00AA00389B71，PCM = 00000001-0000-0010-8000-00AA00389B71。
static const GUID kSubIEEE_FLOAT = {0x00000003,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};
static const GUID kSubPCM        = {0x00000001,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};

// 枚举默认音频输出设备并激活 IAudioClient（共享/独占共用这段逻辑）。
// 失败返回 nullptr；成功后调用者负责释放 tmpDev/tmpEnum。
static IAudioClient *openDefaultClient(IMMDevice **tmpDev, IMMDeviceEnumerator **tmpEnum){
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);   // 会话某处已初始化时返回 S_FALSE，可接受
    IMMDeviceEnumerator *en=nullptr;
    if (FAILED(CoCreateInstance(CLSID_MMDeviceEnumerator, nullptr, CLSCTX_ALL, IID_IMMDeviceEnumerator, (void**)(&en)))) return nullptr;
    IMMDevice *dev=nullptr;
    // ★ 支持按设备名片段挑选（蓝牙耳机有两个条目：A2DP「耳机 (X)」和「耳机 (X) Hands-Free」←后者是电话音质）。
    //   DSH_AUDIO_DEVICE=<片段> 时枚举所有 Render 端点做匹配；没匹配到或未设置 → 用默认端点。
    { const char* want=getenv("DSH_AUDIO_DEVICE");
      if (want && want[0]) {
          IMMDeviceCollection* col=nullptr;
          if (SUCCEEDED(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col)) && col) {
              UINT n=0; col->GetCount(&n);
              for (UINT i=0;i<n && !dev;i++) {
                  IMMDevice* d=nullptr;
                  if (FAILED(col->Item(i,&d)) || !d) continue;
                  IPropertyStore* ps=nullptr; bool hit=false;
                  if (SUCCEEDED(d->OpenPropertyStore(STGM_READ,&ps)) && ps) {
                      PROPVARIANT pv; PropVariantInit(&pv);
                      if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName,&pv)) && pv.vt==VT_LPWSTR && pv.pwszVal) {
                          char nb[256]={0};
                          WideCharToMultiByte(CP_UTF8,0,pv.pwszVal,-1,nb,sizeof(nb)-1,nullptr,nullptr);
                          if (strstr(nb, want)) hit=true;
                      }
                      PropVariantClear(&pv); ps->Release();
                  }
                  if (hit) dev=d; else d->Release();
              }
              col->Release();
          }
      }
    }
    if (!dev && FAILED(en->GetDefaultAudioEndpoint(eRender, eConsole, &dev))) { en->Release(); return nullptr; }
    IAudioClient *ac=nullptr;
    if (FAILED(dev->Activate(IID_IAudioClient, CLSCTX_ALL, nullptr, (void**)(&ac)))) { dev->Release(); en->Release(); return nullptr; }
    *tmpDev=dev; *tmpEnum=en;
    return ac;
}

// 构造一个 WAVEFORMATEXTENSIBLE：给定采样率/声道数/位深(float 或 int16)。
// 独占模式可由此精确指定想要的格式；设备能接受才通过 Initialize。
static void makeExtensible(WAVEFORMATEXTENSIBLE *wfx, int rate, int ch, bool isFloat, int bits){
    memset(wfx, 0, sizeof(*wfx));
    WAVEFORMATEX *wf=&wfx->Format;
    wf->wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    wf->nChannels = (WORD)ch;
    wf->nSamplesPerSec = (DWORD)rate;
    wf->wBitsPerSample = (WORD)bits;
    // nBlockAlign = 声道数 * 每样本字节
    wf->nBlockAlign = (WORD)(ch * (bits/8));
    wf->nAvgBytesPerSec = wf->nSamplesPerSec * wf->nBlockAlign;
    wf->cbSize = 22;                                   // EXTENSIBLE 尾部（不含 WAVEFORMATEX）
    wfx->Samples.wValidBitsPerSample = (WORD)bits;
    // 立体声默认 L+R 扬声器掩码
    wfx->dwChannelMask = (ch>=2) ? (SPEAKER_FRONT_LEFT|SPEAKER_FRONT_RIGHT) : SPEAKER_FRONT_CENTER;
    wfx->SubFormat = isFloat ? kSubIEEE_FLOAT : kSubPCM;
}

// 建一个 IAudioClient 后，尝试用给定的格式以独占模式 Initialize；成功返回该格式对应的参数。
static bool tryExclusiveInit(IAudioClient *ac, int rate, int ch, bool isFloat, int bits,
                             int *outRate, int *outCh, int *outBits, bool *outIsFloat){
    WAVEFORMATEXTENSIBLE wfx;
    makeExtensible(&wfx, rate, ch, isFloat, bits);
    // 独占模式：AUDCLNT_SHAREMODE_EXCLUSIVE；周期性/缓冲时长传 0 让设备取默认。
    HRESULT hr = ac->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, 0, 0, 0, &wfx.Format, nullptr);
    if (FAILED(hr)) return false;
    if (outRate) *outRate=rate; if(outCh)*outCh=ch; if(outBits)*outBits=bits;
    if (outIsFloat) *outIsFloat=isFloat;
    return true;
}

WasapiSink::WasapiSink() {}
WasapiSink::~WasapiSink() { stop(); }

bool WasapiSink::initShared(int *outRate, int *outCh, int *outBits){
    m_comInit = (CoInitializeEx(nullptr, COINIT_MULTITHREADED) != RPC_E_CHANGED_MODE);
    IMMDevice *dev=nullptr; IMMDeviceEnumerator *en=nullptr;
    IAudioClient *ac=openDefaultClient(&dev,&en);
    if(!ac){ return false; }

    WAVEFORMATEX *mix=nullptr;
    if (FAILED(ac->GetMixFormat(&mix))) { ac->Release(); dev->Release(); en->Release(); return false; }
    // 用混音格式初始化共享模式（几乎总能成功）
    HRESULT hr=ac->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, mix, nullptr);
    if (FAILED(hr)) { CoTaskMemFree(mix); ac->Release(); dev->Release(); en->Release(); return false; }

    // 诊断：读设备友好名 + 缓冲大小，看清到底开在哪个设备、缓冲是否正常
    { IPropertyStore* ps=nullptr;
      if (dev && SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps)) && ps) {
          PROPVARIANT pv; PropVariantInit(&pv);
          if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt==VT_LPWSTR && pv.pwszVal) {
              char nb[256]={0};
              WideCharToMultiByte(CP_UTF8,0,pv.pwszVal,-1,nb,sizeof(nb)-1,nullptr,nullptr);
              m_devName = nb;
          }
          PropVariantClear(&pv); ps->Release();
      }
    }
    ac->GetBufferSize(&m_bufferFrames);
    m_outRate = mix->nSamplesPerSec; m_outCh = mix->nChannels; m_outBits = mix->wBitsPerSample;
    m_bytesPerFrame = mix->nBlockAlign;   // = outCh * (bits/8)
    m_outIsFloat = true; m_exclusive = false;
    if (outRate) *outRate=m_outRate; if(outCh)*outCh=m_outCh; if(outBits)*outBits=m_outBits;

    if (FAILED(ac->GetService(IID_IAudioRenderClient, (void**)(&m_render)))) { CoTaskMemFree(mix); ac->Release(); dev->Release(); en->Release(); return false; }
    m_client = ac;   // 保持
    CoTaskMemFree(mix); dev->Release(); en->Release();
    return true;
}

bool WasapiSink::initExclusive(int desiredRate, int desiredCh, int *outRate, int *outCh, int *outBits){
    m_comInit = (CoInitializeEx(nullptr, COINIT_MULTITHREADED) != RPC_E_CHANGED_MODE);
    IMMDevice *dev=nullptr; IMMDeviceEnumerator *en=nullptr;
    IAudioClient *ac=openDefaultClient(&dev,&en);
    if(!ac){ return false; }

    // 先取混音格式（拿到设备原生采样率/声道作兜底参考，同时它也是大多数设备在独占下能用的格式）
    WAVEFORMATEX *mix=nullptr; (void)ac->GetMixFormat(&mix);
    int mixRate = mix ? mix->nSamplesPerSec : 48000;
    int mixCh   = mix ? mix->nChannels : 2;

    // 协商候选列表（优先级从高到低）：float32 / int16，每种再按「首选率→设备混音率→常见的 48k/44.1k」降级。
    // 关键：desiredRate 通常是源采样率（如 44.1k），独占下直接用它→零 SRC、位完美。
    const int wantCh = (desiredCh>0) ? desiredCh : 2;
    bool ok=false;
    // 组装候选率（去掉非法/重复）：首选率（desiredRate>0 才加）→ 设备混音率 → 常见的 48k/44.1k
    int cand[4]; int nc=0;
    if (desiredRate>0) cand[nc++]=desiredRate;
    if (mixRate>0 && !(nc>0 && cand[nc-1]==mixRate)) cand[nc++]=mixRate;
    if (nc==0) cand[nc++]=48000;
    if (nc<4 && cand[nc-1]!=44100) cand[nc++]=44100;
    // float32 尝试（最常见的独占协商结果）
    for (int i=0; !ok && i<nc; ++i)
        ok = tryExclusiveInit(ac, cand[i], wantCh, true, 32, outRate,outCh,outBits,&m_outIsFloat);
    // int16 尝试（float32 概不接受时降级）
    for (int i=0; !ok && i<nc; ++i)
        ok = tryExclusiveInit(ac, cand[i], wantCh, false,16, outRate,outCh,outBits,&m_outIsFloat);

    if (mix) CoTaskMemFree(mix);
    if (!ok){ ac->Release(); dev->Release(); en->Release(); return false; }

    // 协商成功后，用实际协商结果的声道数/位深做后面的字节预算
    // 诊断：读设备友好名 + 缓冲大小，看清到底开在哪个设备、缓冲是否正常
    { IPropertyStore* ps=nullptr;
      if (dev && SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps)) && ps) {
          PROPVARIANT pv; PropVariantInit(&pv);
          if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt==VT_LPWSTR && pv.pwszVal) {
              char nb[256]={0};
              WideCharToMultiByte(CP_UTF8,0,pv.pwszVal,-1,nb,sizeof(nb)-1,nullptr,nullptr);
              m_devName = nb;
          }
          PropVariantClear(&pv); ps->Release();
      }
    }
    ac->GetBufferSize(&m_bufferFrames);
    m_outRate = *outRate; m_outCh = *outCh; m_outBits = *outBits;
    m_bytesPerFrame = (uint32_t)(m_outCh * (m_outBits/8));
    m_exclusive = true;

    if (FAILED(ac->GetService(IID_IAudioRenderClient, (void**)(&m_render)))) { ac->Release(); dev->Release(); en->Release(); return false; }
    m_client = ac;
    dev->Release(); en->Release();
    return true;
}

bool WasapiSink::start(){
    IAudioClient *ac=(IAudioClient*)m_client;
    if (!ac) return false;
    HRESULT hr=ac->Start();
    m_running = (hr==S_OK);
    return m_running;
}

uint32_t WasapiSink::padding() const {
    IAudioClient* ac=(IAudioClient*)m_client;
    if (!ac) return 0;
    UINT32 p=0; ac->GetCurrentPadding(&p);
    return p;
}

void WasapiSink::flush(){
    IAudioClient *ac=(IAudioClient*)m_client;
    if (!ac) return;
    // 共享/独占都允许 Stop → Reset → Start（Reset 只在非运行状态可用）；不释放 COM，约 1ms
    bool wasRunning = m_running;
    ac->Stop();
    ac->Reset();
    if (wasRunning) { ac->Start(); m_running = true; }
}

void WasapiSink::stop(){
    IAudioClient *ac=(IAudioClient*)m_client;
    if (ac) { ac->Stop(); }
    if (m_render) { ((IAudioRenderClient*)m_render)->Release(); m_render=nullptr; }
    if (ac) { ac->Release(); m_client=nullptr; }
    if (m_comInit) { CoUninitialize(); m_comInit=false; }
    m_running=false;
}

uint32_t WasapiSink::freeFrames() const {
    IAudioClient *ac=(IAudioClient*)m_client;
    if (!ac) return 0;
    UINT32 padding=0; ac->GetCurrentPadding(&padding);
    return (padding < m_bufferFrames) ? (m_bufferFrames - padding) : 0;
}

int WasapiSink::writeFloat(const float *interleaved, int frames){
    IAudioRenderClient *rc=(IAudioRenderClient*)m_render;
    if (!rc || frames<=0) return 0;
    uint32_t free = freeFrames();
    unsigned n = (unsigned)std::min<int>(frames, (int)free);
    if (n==0) return 0;
    BYTE *dst=nullptr;
    if (FAILED(rc->GetBuffer(n, &dst))) return 0;

    if (m_outIsFloat){
        // 协商为 float32：按字节直拷（WASAPI 独占/共享都接受 float32）
        memcpy(dst, interleaved, (size_t)n * m_bytesPerFrame);
    } else {
        // 协商为 int16：float[-1,1] → int16 乘 32767，静音/防削波
        short *d16=(short*)dst;
        for (unsigned i=0;i< (size_t)n * (unsigned)m_outCh; ++i){
            // 实时增益控制：软限幅（tanh 近似）替代硬 clip，平滑上限减少削波失真
            float v = interleaved[i];
            v = tanhf(v * 1.2f) / tanhf(1.2f);   // 软拐点限幅，[-1,1] 平滑过渡
            d16[i]=(short)(v*32767.0f + (v>=0?0.5f:-0.5f));
        }
    }
    rc->ReleaseBuffer(n, 0);
    return (int)n;
}
